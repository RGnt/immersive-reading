using System.Diagnostics;
using System.Text.Json;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;
using ImmersiveReading.Library.Flashcards;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class StudyPackRpcModule(
    LibraryManager library,
    PythonStudyPackBridge? studyPackBridge = null,
    FlashcardMiningService? flashcardService = null,
    IRpcEventSink? events = null,
    IConfigurationManager? configManager = null,
    LibraryDatabase? db = null) : IRpcModule, IDisposable
{
    private StudyPackLease? _activeStudyLease;
    private readonly AnkiConnectClient _ankiClient = new();

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("studypack.getOverview", (req, _) => Task.FromResult<object?>(GetOverview(req)));
            yield return ("studypack.build", BuildAsync);
            yield return ("studypack.generate", BuildAsync);
            yield return ("studypack.openPdf", (req, _) => Task.FromResult<object?>(OpenPdf(req)));
            yield return ("studypack.getLearningMaterial", GetLearningMaterialAsync);
            yield return ("studypack.exportTransfer", GetLearningMaterialAsync);
            yield return ("studypack.importTransfer", ImportTransferAsync);
            yield return ("studypack.generateFromTransfer", GenerateFromTransferAsync);
            yield return ("flashcards.mine", MineFlashcardAsync);
            yield return ("flashcards.list", (req, _) => Task.FromResult<object?>(ListFlashcards(req)));
            yield return ("anki.ping", PingAnkiAsync);
            yield return ("anki.getDecks", GetAnkiDecksAsync);
            yield return ("anki.getModels", GetAnkiModelsAsync);
            yield return ("anki.sync", SyncAnkiNotesAsync);
        }
    }

    private object? GetOverview(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        if (studyPackBridge is null)
        {
            return new
            {
                overview = new StudyPackBookOverview(
                    bookId,
                    false,
                    null,
                    null,
                    [],
                    [],
                    [],
                    0,
                    false,
                    null),
                minedCards = Array.Empty<FlashcardSummaryItem>(),
                minedCardCount = 0,
                hasActiveLease = false,
                leaseGeneration = (string?)null
            };
        }

        var (activeGen, _, activeDoc) = studyPackBridge.TryReadActivePublication();
        activeDoc?.Dispose();
        if (!string.IsNullOrEmpty(activeGen) && _activeStudyLease?.Generation != activeGen)
        {
            _activeStudyLease?.Dispose();
            _activeStudyLease = studyPackBridge.AcquireSnapshotLease(activeGen);
        }

        var overview = studyPackBridge.GetBookStudyPack(bookId);
        if (overview.Available && db is not null)
        {
            try
            {
                using var conn = db.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE books SET has_study_pack = 1, updated_at_utc = $now WHERE book_id = $id AND has_study_pack = 0;";
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$id", bookId);
                int affected = cmd.ExecuteNonQuery();
                if (affected > 0)
                {
                    events?.Emit("library.bookUpdated", new { bookId, hasStudyPack = true });
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // Flag refresh is best-effort; the study pack itself already exists.
                System.Diagnostics.Trace.TraceWarning($"Could not set has_study_pack for '{bookId}': {ex.Message}");
            }
        }

        var minedCards = flashcardService?.ListBookCards(bookId) ?? [];
        return new
        {
            overview,
            minedCards,
            minedCardCount = minedCards.Count,
            hasActiveLease = _activeStudyLease is not null,
            leaseGeneration = _activeStudyLease?.Generation
        };
    }

    private async Task<object?> BuildAsync(RpcRequest req, CancellationToken ct)
    {
        if (studyPackBridge is null) throw new InvalidOperationException("Study pack bridge not configured.");
        string bookId = req.RequiredString("bookId");
        bool readerOnly = req.OptionalBool("readerOnly") ?? false;
        var detail = library.GetBookDetail(bookId)
            ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        var progress = new Progress<string>(line =>
        {
            events?.Emit("studypack.buildProgress", new { bookId, line });
        });

        string epubPath = Path.Combine(detail.Summary.FolderPath, detail.Summary.EpubFileName);
        if (!File.Exists(epubPath))
        {
            var matched = Directory.Exists(detail.Summary.FolderPath)
                ? Directory.GetFiles(detail.Summary.FolderPath, "*.epub").FirstOrDefault()
                : null;
            if (matched is not null) epubPath = matched;
        }

        string? deckCsvPath = req.OptionalString("deckCsvPath") ?? req.OptionalString("deckPath");
        if (string.IsNullOrEmpty(deckCsvPath) && Directory.Exists(detail.Summary.FolderPath))
        {
            deckCsvPath = Directory.GetFiles(detail.Summary.FolderPath, "*.csv").FirstOrDefault();
        }

        var options = new StudyPackOptions(DeckCsvPath: deckCsvPath, IncludeA4Pdf: !readerOnly, ReaderOnly: readerOnly);
        var result = await studyPackBridge
            .BuildStudyPackAsync(detail.Summary, epubPath, options, progress, ct)
            .ConfigureAwait(false);

        if (result.Success && db is not null)
        {
            try
            {
                using var conn = db.OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE books SET has_study_pack = 1, updated_at_utc = $now WHERE book_id = $id;";
                cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("$id", bookId);
                cmd.ExecuteNonQuery();

                events?.Emit("library.bookUpdated", new { bookId, hasStudyPack = true });
            }
            catch { }
        }

        return result;
    }

    private static object? OpenPdf(RpcRequest req)
    {
        string fullPath = req.RequiredString("fullPath");
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"PDF file not found: '{fullPath}'.");
        }

        bool launchExternal = req.OptionalBool("launchExternal") ?? true;
        if (launchExternal)
        {
            Process.Start(new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true
            });
        }
        return new { ok = true, fullPath };
    }

    private async Task<object?> GetLearningMaterialAsync(RpcRequest req, CancellationToken ct)
    {
        string bookId = req.RequiredString("bookId");
        string outputDir = studyPackBridge?.OutputRoot ?? Path.Combine(Directory.GetCurrentDirectory(), "output");

        string[] candidatePaths = [
            Path.Combine(outputDir, "data", bookId, "learning_material.json"),
            Path.Combine(outputDir, "data", bookId, "study_transfer.json"),
            Path.Combine(outputDir, "learning_material.json"),
            Path.Combine(outputDir, "study_data.json"),
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                return new
                {
                    bookId,
                    filePath = path,
                    learningMaterial = doc.RootElement.Clone()
                };
            }
        }

        return new { bookId, filePath = (string?)null, learningMaterial = (object?)null };
    }

    private async Task<object?> ImportTransferAsync(RpcRequest req, CancellationToken ct)
    {
        if (studyPackBridge is null) throw new InvalidOperationException("Study pack bridge not configured.");
        string bookId = req.RequiredString("bookId");
        string? transferJson = req.OptionalString("transferJson");
        string? sourceFilePath = req.OptionalString("sourceFilePath");

        if (string.IsNullOrEmpty(transferJson) && !string.IsNullOrEmpty(sourceFilePath) && File.Exists(sourceFilePath))
        {
            transferJson = await File.ReadAllTextAsync(sourceFilePath, ct);
        }

        if (string.IsNullOrEmpty(transferJson))
        {
            throw new ArgumentException("Either 'transferJson' or 'sourceFilePath' must be provided.");
        }

        // Validate JSON
        using var doc = JsonDocument.Parse(transferJson);

        string outputDir = studyPackBridge.OutputRoot;
        string bookDataDir = Path.Combine(outputDir, "data", bookId);
        Directory.CreateDirectory(bookDataDir);

        string targetPath = Path.Combine(bookDataDir, "learning_material.json");
        await File.WriteAllTextAsync(targetPath, transferJson, ct);
        await File.WriteAllTextAsync(Path.Combine(bookDataDir, "study_transfer.json"), transferJson, ct);

        bool generatePdfs = req.OptionalBool("generatePdfs") ?? true;
        if (generatePdfs)
        {
            var progress = new Progress<string>(line =>
            {
                events?.Emit("studypack.buildProgress", new { bookId, line });
            });
            var buildRes = await studyPackBridge.GenerateFromTransferAsync(bookId, targetPath, progress, ct);
            return new { ok = buildRes.Success, bookId, path = targetPath, buildResult = buildRes };
        }

        return new { ok = true, bookId, path = targetPath };
    }

    private async Task<object?> GenerateFromTransferAsync(RpcRequest req, CancellationToken ct)
    {
        if (studyPackBridge is null) throw new InvalidOperationException("Study pack bridge not configured.");
        string bookId = req.RequiredString("bookId");
        string outputDir = studyPackBridge.OutputRoot;
        string targetPath = Path.Combine(outputDir, "data", bookId, "learning_material.json");
        if (!File.Exists(targetPath))
        {
            targetPath = Path.Combine(outputDir, "learning_material.json");
        }
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"Learning material transfer file not found for book '{bookId}'.");
        }

        var progress = new Progress<string>(line =>
        {
            events?.Emit("studypack.buildProgress", new { bookId, line });
        });
        var buildRes = await studyPackBridge.GenerateFromTransferAsync(bookId, targetPath, progress, ct);
        return new { ok = buildRes.Success, bookId, path = targetPath, buildResult = buildRes };
    }

    private async Task<object?> MineFlashcardAsync(RpcRequest req, CancellationToken ct)
    {
        if (flashcardService is null) throw new InvalidOperationException("Flashcard mining service not configured.");
        string bookId = req.RequiredString("bookId");
        string bookTitle = req.OptionalString("bookTitle") ?? bookId;
        string chapterHref = req.OptionalString("chapterHref") ?? "";
        int chapterNumber = req.OptionalInt("chapterNumber") ?? 1;
        string chapterTitle = req.OptionalString("chapterTitle") ?? $"Chapter {chapterNumber:D2}";
        string word = req.OptionalString("word") ?? req.RequiredString("expression");
        string reading = req.OptionalString("reading") ?? "";
        string meaningGloss = req.OptionalString("meaningGloss") ?? req.OptionalString("meaning") ?? "";
        string sentenceText = req.RequiredString("sentenceText");

        var mined = flashcardService.MineCard(new MineFlashcardRequest(
            BookId: bookId,
            BookTitle: bookTitle,
            ChapterHref: chapterHref,
            ChapterNumber: chapterNumber,
            ChapterTitle: chapterTitle,
            Expression: word,
            Reading: reading,
            Meaning: meaningGloss,
            SentenceText: sentenceText));

        events?.Emit("flashcards.mined", new { bookId, card = mined.Card, totalBookCards = mined.TotalBookCards });

        var dictConfig = configManager?.GetConfig().Dictionaries ?? new DictionarySettings();
        bool shouldSyncAnki = req.OptionalBool("syncToAnki") ?? dictConfig.AutoSyncAnki;

        AnkiSyncResult? ankiSync = null;
        if (shouldSyncAnki)
        {
            ankiSync = await _ankiClient.SyncNotesAsync(
                endpointUrl: req.OptionalString("ankiUrl") ?? dictConfig.AnkiConnectUrl,
                deckName: req.OptionalString("ankiDeck") ?? dictConfig.AnkiDeckName,
                modelName: req.OptionalString("ankiModel") ?? dictConfig.AnkiModelName,
                notes:
                [
                    new AnkiConnectNoteRequest(
                        Expression: word,
                        Reading: reading,
                        Meaning: meaningGloss,
                        Sentence: sentenceText,
                        Source: $"{bookTitle} - {chapterTitle}",
                        Tags: ["ImmersiveReading", bookId]
                    )
                ],
                ct: ct
            ).ConfigureAwait(false);
        }

        return new
        {
            mined.CardId,
            mined.LibraryJsonPath,
            mined.AnkiTsvPath,
            mined.TotalBookCards,
            mined.Card,
            Anki = ankiSync
        };
    }

    private async Task<object?> PingAnkiAsync(RpcRequest req, CancellationToken ct)
    {
        var dictConfig = configManager?.GetConfig().Dictionaries ?? new DictionarySettings();
        string url = req.OptionalString("url") ?? dictConfig.AnkiConnectUrl;
        var (reachable, version, error) = await _ankiClient.PingAsync(url, ct).ConfigureAwait(false);
        return new { reachable, version, error, url };
    }

    private async Task<object?> GetAnkiDecksAsync(RpcRequest req, CancellationToken ct)
    {
        var dictConfig = configManager?.GetConfig().Dictionaries ?? new DictionarySettings();
        string url = req.OptionalString("url") ?? dictConfig.AnkiConnectUrl;
        var decks = await _ankiClient.GetDeckNamesAsync(url, ct).ConfigureAwait(false);
        return new { decks };
    }

    private async Task<object?> GetAnkiModelsAsync(RpcRequest req, CancellationToken ct)
    {
        var dictConfig = configManager?.GetConfig().Dictionaries ?? new DictionarySettings();
        string url = req.OptionalString("url") ?? dictConfig.AnkiConnectUrl;
        var models = await _ankiClient.GetModelNamesAsync(url, ct).ConfigureAwait(false);
        return new { models };
    }

    private async Task<object?> SyncAnkiNotesAsync(RpcRequest req, CancellationToken ct)
    {
        var dictConfig = configManager?.GetConfig().Dictionaries ?? new DictionarySettings();
        string url = req.OptionalString("url") ?? dictConfig.AnkiConnectUrl;
        string deck = req.OptionalString("deck") ?? dictConfig.AnkiDeckName;
        string model = req.OptionalString("model") ?? dictConfig.AnkiModelName;

        var noteList = new List<AnkiConnectNoteRequest>();

        // 1. Single card payload
        string? expr = req.OptionalString("expression") ?? req.OptionalString("word");
        if (!string.IsNullOrWhiteSpace(expr))
        {
            noteList.Add(new AnkiConnectNoteRequest(
                Expression: expr,
                Reading: req.OptionalString("reading") ?? "",
                Meaning: req.OptionalString("meaning") ?? req.OptionalString("meaningGloss") ?? "",
                Sentence: req.OptionalString("sentence") ?? req.OptionalString("sentenceText") ?? "",
                Source: req.OptionalString("source"),
                Tags: ["ImmersiveReading"]
            ));
        }

        // 2. BookId cards (sync all mined cards for book)
        string? bookId = req.OptionalString("bookId");
        if (!string.IsNullOrWhiteSpace(bookId) && flashcardService != null)
        {
            var bookCards = flashcardService.ListBookCards(bookId);
            foreach (var bc in bookCards)
            {
                noteList.Add(new AnkiConnectNoteRequest(
                    Expression: bc.Prompt,
                    Reading: bc.Reading,
                    Meaning: bc.Meaning,
                    Sentence: bc.Example,
                    Source: $"{bc.ChapterTitle}",
                    Tags: ["ImmersiveReading", bookId]
                ));
            }
        }

        if (noteList.Count == 0)
        {
            return new AnkiSyncResult(true, 0, 0, 0, "No notes found to sync.");
        }

        var res = await _ankiClient.SyncNotesAsync(url, deck, model, noteList, ct).ConfigureAwait(false);
        return res;
    }

    private object? ListFlashcards(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        var cards = flashcardService?.ListBookCards(bookId) ?? [];
        return new { bookId, cards, count = cards.Count };
    }

    public void Dispose()
    {
        _activeStudyLease?.Dispose();
        _activeStudyLease = null;
    }
}

