using System.Diagnostics;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class StudyPackRpcModule(
    LibraryManager library,
    PythonStudyPackBridge? studyPackBridge = null,
    FlashcardMiningService? flashcardService = null,
    IRpcEventSink? events = null) : IRpcModule, IDisposable
{
    private StudyPackLease? _activeStudyLease;

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("studypack.getOverview", (req, _) => Task.FromResult<object?>(GetOverview(req)));
            yield return ("studypack.build", BuildAsync);
            yield return ("studypack.openPdf", (req, _) => Task.FromResult<object?>(OpenPdf(req)));
            yield return ("flashcards.mine", (req, _) => Task.FromResult<object?>(MineFlashcard(req)));
            yield return ("flashcards.list", (req, _) => Task.FromResult<object?>(ListFlashcards(req)));
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

        return await studyPackBridge
            .BuildStudyPackAsync(bookId, detail.Summary.FolderPath, readerOnly, progress, ct)
            .ConfigureAwait(false);
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

    private object? MineFlashcard(RpcRequest req)
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
        return mined;
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

