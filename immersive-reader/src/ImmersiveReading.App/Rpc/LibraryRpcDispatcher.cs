using System.Text.Json;
using System.Text.Json.Serialization;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc;

public sealed class LibraryRpcDispatcher
{
    private readonly LibraryManager _library;
    private readonly LibraryDatabase? _db;
    private readonly BookSchemeHandler? _bookSchemeHandler;
    private readonly string? _workspaceBooksDir;
    private readonly Action<string>? _sendMessageToUi;
    private readonly DictionaryLookupEngine? _lookupEngine;
    private readonly VocabularyTracker? _vocabTracker;
    private readonly DictionaryDatabase? _dictDb;
    private readonly PythonStudyPackBridge? _studyPackBridge;
    private readonly FlashcardMiningService? _flashcardService;
    private readonly FeaturePluginHost? _featurePluginHost;
    private readonly Action<bool, string>? _onSelfTestCompleted;
    private readonly IConfigurationManager? _configManager;
    private StudyPackLease? _activeStudyLease;
    private bool _bootstrappedWorkspaceBooks;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public LibraryRpcDispatcher(
        LibraryManager library,
        LibraryDatabase? db = null,
        BookSchemeHandler? bookSchemeHandler = null,
        string? workspaceBooksDir = null,
        Action<string>? sendMessageToUi = null,
        DictionaryLookupEngine? lookupEngine = null,
        VocabularyTracker? vocabTracker = null,
        DictionaryDatabase? dictDb = null,
        PythonStudyPackBridge? studyPackBridge = null,
        FlashcardMiningService? flashcardService = null,
        FeaturePluginHost? featurePluginHost = null,
        Action<bool, string>? onSelfTestCompleted = null,
        IConfigurationManager? configManager = null)
    {
        _library = library;
        _db = db;
        _bookSchemeHandler = bookSchemeHandler;
        _workspaceBooksDir = workspaceBooksDir;
        _sendMessageToUi = sendMessageToUi;
        _lookupEngine = lookupEngine;
        _vocabTracker = vocabTracker;
        _dictDb = dictDb;
        _studyPackBridge = studyPackBridge;
        _flashcardService = flashcardService;
        _featurePluginHost = featurePluginHost;
        _onSelfTestCompleted = onSelfTestCompleted;
        _configManager = configManager;
    }

    public async Task<string> DispatchJsonAsync(string rawRequestJson, CancellationToken ct = default)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(rawRequestJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idProp))
            {
                id = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.GetRawText();
            }

            string method = root.TryGetProperty("method", out var mProp) ? (mProp.GetString() ?? "") : "";
            JsonElement @params = root.TryGetProperty("params", out var pProp) ? pProp : default;

            object? result = await HandleMethodAsync(method, @params, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { id, result, error = (object?)null }, JsonOpts);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                result = (object?)null,
                error = new { message = ex.Message, type = ex.GetType().Name }
            }, JsonOpts);
        }
    }

    public async Task<object?> HandleMethodAsync(string method, JsonElement p, CancellationToken ct = default)
    {
        switch (method)
        {
            case "library.getBootstrapState":
            {
                await EnsureInitialStarterLibraryAsync(ct).ConfigureAwait(false);
                var folders = _library.GetFolderTree();
                var tags = _library.ListTags();
                var customFields = _library.ListCustomFieldDefinitions();
                var initialPage = _library.ListBooks(new BookListQuery(Limit: 100));
                var config = _configManager?.GetConfig() ?? AppConfig.CreateDefault();
                string defaultScanDir = !string.IsNullOrWhiteSpace(config.Directories.DefaultScanDirectory)
                    ? config.Directories.DefaultScanDirectory
                    : (_workspaceBooksDir ?? "");
                return new
                {
                    folders,
                    tags,
                    customFields,
                    initialPage,
                    defaultScanDir,
                    config
                };
            }

            case "library.listBooks":
            {
                var query = p.ValueKind is JsonValueKind.Object
                    ? JsonSerializer.Deserialize<BookListQuery>(p.GetRawText(), JsonOpts) ?? new BookListQuery()
                    : new BookListQuery();
                return _library.ListBooks(query);
            }

            case "library.getBookDetail":
            {
                string bookId = GetRequiredString(p, "bookId");
                return _library.GetBookDetail(bookId)
                    ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");
            }

            case "library.importBook":
            {
                string epubPath = GetRequiredString(p, "epubPath");
                string? folderId = GetOptionalString(p, "folderId");
                string? seriesName = GetOptionalString(p, "seriesName");
                double? seriesIdx = GetOptionalDouble(p, "seriesIndex");
                return await _library.ImportBookAsync(
                    epubPath,
                    folderId,
                    seriesName,
                    seriesIdx,
                    ct: ct).ConfigureAwait(false);
            }

            case "library.scanDirectory":
            {
                string rootDir = GetRequiredString(p, "rootDirectory");
                string? targetFolderId = GetOptionalString(p, "targetFolderId");
                bool recursive = !p.TryGetProperty("recursive", out var recProp) || recProp.GetBoolean();
                bool autoFolders = p.TryGetProperty("autoCreateFolders", out var afProp) && afProp.GetBoolean();

                var progress = new Progress<ScanBatchProgress>(prog =>
                {
                    EmitEvent("library.scanProgress", prog);
                });

                return await _library.ScanDirectoryAsync(
                    rootDir,
                    targetFolderId,
                    recursive,
                    autoFolders,
                    progress,
                    ct).ConfigureAwait(false);
            }

            case "library.updateBookMetadata":
            {
                var req = JsonSerializer.Deserialize<BookMetadataUpdateRequest>(p.GetRawText(), JsonOpts)
                    ?? throw new ArgumentException("Invalid updateBookMetadata payload.");
                return _library.UpdateBookMetadata(req);
            }

            case "library.bulkUpdateBooks":
            {
                var patch = JsonSerializer.Deserialize<BulkBookUpdatePatch>(p.GetRawText(), JsonOpts)
                    ?? throw new ArgumentException("Invalid bulkUpdateBooks payload.");
                int updatedCount = _library.BulkUpdateBooks(patch);
                return new { updatedCount };
            }

            case "library.resetBookMetadataToOpf":
            {
                string bookId = GetRequiredString(p, "bookId");
                return _library.ResetBookMetadataToOpf(bookId);
            }

            case "library.deleteBook":
            {
                string bookId = GetRequiredString(p, "bookId");
                _library.DeleteBook(bookId);
                return new { ok = true };
            }

            case "library.bulkDeleteBooks":
            {
                var bookIds = GetStringList(p, "bookIds");
                int deleted = _library.BulkDeleteBooks(bookIds);
                return new { ok = true, deletedCount = deleted };
            }

            case "library.pruneEmptyFolders":
            {
                var pruned = _library.PruneEmptyFolders();
                return new { ok = true, prunedCount = pruned.Count, prunedFolderNames = pruned, tree = _library.GetFolderTree() };
            }

            case "library.moveFolder":
            {
                string folderId = GetRequiredString(p, "folderId");
                string? newParentId = GetOptionalString(p, "newParentFolderId");
                int? newSortOrder = p.TryGetProperty("newSortOrder", out var so) && so.TryGetInt32(out int sVal) ? sVal : null;
                bool moved = _library.MoveFolder(folderId, newParentId, newSortOrder);
                return new { ok = moved, tree = _library.GetFolderTree() };
            }

            case "library.reorderBooks":
            {
                var bookIds = GetStringList(p, "bookIdsInOrder");
                _library.ReorderBooks(bookIds);
                return new { ok = true };
            }

            case "library.getFolderTree":
                return _library.GetFolderTree();

            case "library.saveFolder":
            {
                string? folderId = GetOptionalString(p, "folderId");
                string? parentFolderId = GetOptionalString(p, "parentFolderId");
                string name = GetRequiredString(p, "name");
                string folderKind = GetOptionalString(p, "folderKind") ?? "folder";
                string description = GetOptionalString(p, "description") ?? "";
                int sortOrder = p.TryGetProperty("sortOrder", out var so) && so.TryGetInt32(out int sVal) ? sVal : 0;
                string defaultSort = GetOptionalString(p, "defaultBookSort") ?? "series_or_title";

                List<CustomFieldValueInput>? customFields = null;
                if (p.TryGetProperty("customFields", out var cfEl) && cfEl.ValueKind == JsonValueKind.Array)
                {
                    customFields = JsonSerializer.Deserialize<List<CustomFieldValueInput>>(cfEl.GetRawText(), JsonOpts);
                }

                var saved = _library.SaveFolder(
                    folderId,
                    parentFolderId,
                    name,
                    folderKind,
                    description,
                    sortOrder: sortOrder,
                    defaultBookSort: defaultSort,
                    customFields: customFields);
                return new { saved, tree = _library.GetFolderTree() };
            }

            case "library.deleteFolder":
            {
                string folderId = GetRequiredString(p, "folderId");
                _library.DeleteFolder(folderId, reassignBooksToParent: true);
                return new { ok = true, tree = _library.GetFolderTree() };
            }

            case "library.listTags":
                return _library.ListTags();

            case "library.saveTag":
            {
                string? tagId = GetOptionalString(p, "tagId");
                string name = GetRequiredString(p, "name");
                string colorHex = GetOptionalString(p, "colorHex") ?? "#3b82f6";
                int sortOrder = p.TryGetProperty("sortOrder", out var so) && so.TryGetInt32(out int sVal) ? sVal : 0;
                var tag = _library.SaveTag(tagId, name, colorHex, sortOrder);
                return new { tag, tags = _library.ListTags() };
            }

            case "library.deleteTag":
            {
                string tagId = GetRequiredString(p, "tagId");
                _library.DeleteTag(tagId);
                return new { ok = true, tags = _library.ListTags() };
            }

            case "library.listCustomFieldDefs":
                return _library.ListCustomFieldDefinitions();

            case "library.saveCustomFieldDef":
            {
                string fieldKey = GetRequiredString(p, "fieldKey");
                string displayName = GetRequiredString(p, "displayName");
                string fieldType = GetOptionalString(p, "fieldType") ?? "text";
                string targetScope = GetOptionalString(p, "targetScope") ?? "any";
                bool inherit = !p.TryGetProperty("inheritToChildren", out var inh) || inh.GetBoolean();
                bool showOnCard = !p.TryGetProperty("showOnCard", out var soc) || soc.GetBoolean();
                int sortOrder = p.TryGetProperty("sortOrder", out var so) && so.TryGetInt32(out int sVal) ? sVal : 100;

                var def = _library.SaveCustomFieldDefinition(
                    fieldKey,
                    displayName,
                    fieldType,
                    targetScope,
                    inherit,
                    showOnCard: showOnCard,
                    sortOrder: sortOrder);
                return new { def, customFields = _library.ListCustomFieldDefinitions() };
            }

            case "library.setCustomFieldValue":
            {
                string entityType = GetRequiredString(p, "entityType");
                string entityId = GetRequiredString(p, "entityId");
                string fieldKey = GetRequiredString(p, "fieldKey");
                string valueText = GetOptionalString(p, "valueText") ?? "";
                double? valueNumber = GetOptionalDouble(p, "valueNumber");
                _library.SetCustomFieldValue(entityType, entityId, fieldKey, valueText, valueNumber);
                return new { ok = true };
            }

            case "reader.openBook":
            {
                string bookId = GetRequiredString(p, "bookId");
                string? requestedSpineHref = GetOptionalString(p, "spineHref");
                var detail = _library.GetBookDetail(bookId)
                    ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

                string initialSpineHref = "";
                int initialChapterIndex = 0;
                int locatorTokenId = 0;
                int locatorCharOffset = 0;
                double overallCompletion = 0;

                if (!string.IsNullOrWhiteSpace(requestedSpineHref))
                {
                    initialSpineHref = requestedSpineHref;
                    var matchingCh = detail.Chapters.FirstOrDefault(c =>
                        string.Equals(c.SpineHref, requestedSpineHref, StringComparison.OrdinalIgnoreCase));
                    if (matchingCh is not null)
                    {
                        initialChapterIndex = matchingCh.ChapterIndex;
                    }
                }
                else if (_db is not null)
                {
                    using var conn = _db.OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT current_chapter_index, current_spine_href, locator_token_id, locator_char_offset, overall_completion FROM reading_progress WHERE book_id = $id;";
                    cmd.Parameters.AddWithValue("$id", bookId);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        initialChapterIndex = r.GetInt32(0);
                        initialSpineHref = r.GetString(1);
                        locatorTokenId = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                        locatorCharOffset = r.GetInt32(3);
                        overallCompletion = r.GetDouble(4);
                    }
                }

                if (string.IsNullOrEmpty(initialSpineHref) && detail.Chapters.Count > 0)
                {
                    var firstValid = detail.Chapters.FirstOrDefault(c => !c.IsExcluded) ?? detail.Chapters[0];
                    initialSpineHref = firstValid.SpineHref;
                    initialChapterIndex = firstValid.ChapterIndex;
                }

                // Sync writingMode option with book's preferred writing mode
                if (_bookSchemeHandler is not null)
                {
                    string targetWritingMode = detail.Summary.WritingMode == "horizontal-tb" ? "horizontal-tb" : "vertical-rl";
                    var cur = _bookSchemeHandler.CurrentOptions;
                    if (cur.WritingMode != targetWritingMode)
                    {
                        _bookSchemeHandler.UpdateEnrichmentOptions(cur with { WritingMode = targetWritingMode });
                    }
                }

                string chapterUrl = $"ir-book://{bookId}/{initialSpineHref}";
                var currentOptions = _bookSchemeHandler?.CurrentOptions ?? new EnrichmentOptions();
                var enriched = _bookSchemeHandler?.GetEnrichedChapter(bookId, initialSpineHref);

                return new
                {
                    book = detail,
                    initialChapterIndex,
                    initialSpineHref,
                    initialUrl = chapterUrl,
                    initialChapterHtml = enriched?.EnrichedHtml ?? "",
                    locatorTokenId,
                    locatorCharOffset,
                    overallCompletion,
                    enrichmentOptions = new
                    {
                        furiganaMode = currentOptions.FuriganaMode.ToString(),
                        highlightVocabulary = currentOptions.HighlightVocabulary,
                        writingMode = currentOptions.WritingMode,
                        theme = currentOptions.Theme
                    }
                };
            }

            case "reader.getChapterContent":
            {
                string bookId = GetRequiredString(p, "bookId");
                string spineHref = GetRequiredString(p, "spineHref");
                var enriched = _bookSchemeHandler?.GetEnrichedChapter(bookId, spineHref);
                return new
                {
                    bookId,
                    spineHref,
                    url = $"ir-book://{bookId}/{spineHref}",
                    html = enriched?.EnrichedHtml ?? "",
                    tokenCount = enriched?.TokenMap.Tokens.Count ?? 0
                };
            }

            case "reader.saveProgress":
            {
                string bookId = GetRequiredString(p, "bookId");
                int chapterIndex = p.TryGetProperty("chapterIndex", out var ci) ? ci.GetInt32() : 0;
                string spineHref = GetRequiredString(p, "spineHref");
                int? tokenId = p.TryGetProperty("tokenId", out var tid) && tid.ValueKind == JsonValueKind.Number ? tid.GetInt32() : null;
                int charOffset = p.TryGetProperty("charOffset", out var co) ? co.GetInt32() : 0;
                double ratio = p.TryGetProperty("ratio", out var rat) ? rat.GetDouble() : 0;
                double completion = p.TryGetProperty("completion", out var comp) ? comp.GetDouble() : 0;

                if (_db is not null)
                {
                    string now = DateTime.UtcNow.ToString("O");
                    using var conn = _db.OpenConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO reading_progress (
                            book_id, current_chapter_index, current_spine_href, locator_token_id,
                            locator_char_offset, scroll_or_page_ratio, overall_completion, updated_at_utc
                        ) VALUES (
                            $id, $chIdx, $href, $tok, $offset, $ratio, $comp, $now
                        )
                        ON CONFLICT(book_id) DO UPDATE SET
                            current_chapter_index = excluded.current_chapter_index,
                            current_spine_href = excluded.current_spine_href,
                            locator_token_id = excluded.locator_token_id,
                            locator_char_offset = excluded.locator_char_offset,
                            scroll_or_page_ratio = excluded.scroll_or_page_ratio,
                            overall_completion = excluded.overall_completion,
                            updated_at_utc = excluded.updated_at_utc;
                        """;
                    cmd.Parameters.AddWithValue("$id", bookId);
                    cmd.Parameters.AddWithValue("$chIdx", chapterIndex);
                    cmd.Parameters.AddWithValue("$href", spineHref);
                    cmd.Parameters.AddWithValue("$tok", (object?)tokenId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$offset", charOffset);
                    cmd.Parameters.AddWithValue("$ratio", ratio);
                    cmd.Parameters.AddWithValue("$comp", completion);
                    cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();

                    using var updBook = conn.CreateCommand();
                    updBook.CommandText = "UPDATE books SET last_opened_at_utc = $now WHERE book_id = $id;";
                    updBook.Parameters.AddWithValue("$id", bookId);
                    updBook.Parameters.AddWithValue("$now", now);
                    updBook.ExecuteNonQuery();
                }

                return new { ok = true };
            }

            case "reader.setEnrichmentOptions":
            {
                if (_bookSchemeHandler is not null)
                {
                    string fModeStr = GetOptionalString(p, "furiganaMode") ?? "AllKanji";
                    bool highlight = p.TryGetProperty("highlightVocabulary", out var hl) && hl.GetBoolean();
                    string writingMode = GetOptionalString(p, "writingMode") ?? "auto";
                    string theme = GetOptionalString(p, "theme") ?? "paper";

                    Enum.TryParse<FuriganaMode>(fModeStr, ignoreCase: true, out var fMode);
                    var newOptions = new EnrichmentOptions(fMode, highlight, writingMode, theme);
                    _bookSchemeHandler.UpdateEnrichmentOptions(newOptions);
                    return new { ok = true, options = newOptions };
                }
                return new { ok = true };
            }

            case "lookup.query":
            {
                if (_lookupEngine is null) throw new InvalidOperationException("Lookup engine not available.");
                string bookId = GetRequiredString(p, "bookId");
                string chapterHref = GetRequiredString(p, "chapterHref");
                int tokenId = p.GetProperty("tokenId").GetInt32();
                int charOffset = p.TryGetProperty("charOffsetInToken", out var co) ? co.GetInt32() : 0;
                var res = await _lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, tokenId, charOffset), ct);
                return res;
            }

            case "vocab.setStatus":
            {
                if (_vocabTracker is null) throw new InvalidOperationException("Vocabulary tracker not available.");
                string lang = GetOptionalString(p, "language") ?? "ja";
                string lemma = GetRequiredString(p, "lemma");
                string reading = GetRequiredString(p, "reading");
                int statusInt = p.GetProperty("status").GetInt32();
                string? gloss = GetOptionalString(p, "customGloss");
                var status = (WordStatus)statusInt;
                _vocabTracker.SetStatus(lang, lemma, reading, status, gloss);
                EmitEvent("vocab.statusChanged", new { language = lang, lemma, reading, status = statusInt });
                return new { ok = true, lemma, status = statusInt };
            }

            case "dictionaries.list":
            {
                if (_dictDb is null) return new { dictionaries = Array.Empty<object>() };
                var dicts = new List<object>();
                using var conn = _dictDb.CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT dictionary_id, title, revision, language, priority_order, enabled, imported_at_utc FROM dictionary_sources ORDER BY priority_order ASC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    dicts.Add(new
                    {
                        dictionaryId = r.GetString(0),
                        title = r.GetString(1),
                        revision = r.GetString(2),
                        language = r.GetString(3),
                        priorityOrder = r.GetInt32(4),
                        enabled = r.GetInt32(5) == 1,
                        importedAtUtc = r.GetString(6)
                    });
                }
                return new { dictionaries = dicts };
            }

            case "dictionaries.importYomitan":
            {
                if (_dictDb is null) throw new InvalidOperationException("Dictionary database not available.");
                string zipPath = GetRequiredString(p, "zipPath");
                int priority = p.TryGetProperty("priorityOrder", out var po) ? po.GetInt32() : 100;
                var importer = new YomitanSqliteImporter(_dictDb);
                var res = importer.ImportZip(zipPath, priority, ct);
                return res;
            }

            case "studypack.getOverview":
            {
                string bookId = GetRequiredString(p, "bookId");
                if (_studyPackBridge is null)
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

                var (activeGen, _, activeDoc) = _studyPackBridge.TryReadActivePublication();
                activeDoc?.Dispose();
                if (!string.IsNullOrEmpty(activeGen) && _activeStudyLease?.Generation != activeGen)
                {
                    _activeStudyLease?.Dispose();
                    _activeStudyLease = _studyPackBridge.AcquireSnapshotLease(activeGen);
                }

                var overview = _studyPackBridge.GetBookStudyPack(bookId);
                var minedCards = _flashcardService?.ListBookCards(bookId) ?? [];
                return new
                {
                    overview,
                    minedCards,
                    minedCardCount = minedCards.Count,
                    hasActiveLease = _activeStudyLease is not null,
                    leaseGeneration = _activeStudyLease?.Generation
                };
            }

            case "studypack.build":
            {
                if (_studyPackBridge is null) throw new InvalidOperationException("Study pack bridge not configured.");
                string bookId = GetRequiredString(p, "bookId");
                bool readerOnly = p.TryGetProperty("readerOnly", out var ro) && ro.GetBoolean();
                var detail = _library.GetBookDetail(bookId)
                    ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

                var progress = new Progress<string>(line =>
                {
                    EmitEvent("studypack.buildProgress", new { bookId, line });
                });

                var res = await _studyPackBridge
                    .BuildStudyPackAsync(bookId, detail.Summary.FolderPath, readerOnly, progress, ct)
                    .ConfigureAwait(false);
                return res;
            }

            case "studypack.openPdf":
            {
                string fullPath = GetRequiredString(p, "fullPath");
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"PDF file not found: '{fullPath}'.");
                }

                bool launchExternal = !p.TryGetProperty("launchExternal", out var le) || le.GetBoolean();
                if (launchExternal)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath)
                    {
                        UseShellExecute = true
                    });
                }
                return new { ok = true, fullPath };
            }

            case "flashcards.mine":
            {
                if (_flashcardService is null) throw new InvalidOperationException("Flashcard mining service not configured.");
                string bookId = GetRequiredString(p, "bookId");
                string bookTitle = GetOptionalString(p, "bookTitle") ?? bookId;
                string chapterHref = GetOptionalString(p, "chapterHref") ?? "";
                int chapterNumber = p.TryGetProperty("chapterNumber", out var cn) && cn.TryGetInt32(out int cnVal) ? cnVal : 1;
                string chapterTitle = GetOptionalString(p, "chapterTitle") ?? $"Chapter {chapterNumber:D2}";
                string word = GetOptionalString(p, "word") ?? GetRequiredString(p, "expression");
                string reading = GetOptionalString(p, "reading") ?? "";
                string meaningGloss = GetOptionalString(p, "meaningGloss") ?? GetOptionalString(p, "meaning") ?? "";
                string sentenceText = GetRequiredString(p, "sentenceText");

                var mined = _flashcardService.MineCard(new MineFlashcardRequest(
                    BookId: bookId,
                    BookTitle: bookTitle,
                    ChapterHref: chapterHref,
                    ChapterNumber: chapterNumber,
                    ChapterTitle: chapterTitle,
                    Expression: word,
                    Reading: reading,
                    Meaning: meaningGloss,
                    SentenceText: sentenceText));

                EmitEvent("flashcards.mined", new { bookId, card = mined.Card, totalBookCards = mined.TotalBookCards });
                return mined;
            }

            case "flashcards.list":
            {
                string bookId = GetRequiredString(p, "bookId");
                var cards = _flashcardService?.ListBookCards(bookId) ?? [];
                return new { bookId, cards, count = cards.Count };
            }

            case "ui.reportSelfTestResult":
            {
                bool passed = p.TryGetProperty("passed", out var pp) && pp.GetBoolean();
                string summary = GetOptionalString(p, "summary") ?? "";
                _onSelfTestCompleted?.Invoke(passed, summary);
                return new { ok = true };
            }

            case "config.getAll":
            {
                return _configManager?.GetConfig() ?? AppConfig.CreateDefault();
            }

            case "config.getCategory":
            {
                string cat = GetRequiredString(p, "category").ToLowerInvariant();
                var cfg = _configManager?.GetConfig() ?? AppConfig.CreateDefault();
                return cat switch
                {
                    "directories" => (object)cfg.Directories,
                    "appearance" => cfg.Appearance,
                    "reader" => cfg.Reader,
                    "llm" => cfg.Llm,
                    "dictionaries" => cfg.Dictionaries,
                    _ => throw new ArgumentException($"Unknown category '{cat}'.")
                };
            }

            case "config.update":
            {
                if (_configManager is null) throw new InvalidOperationException("Configuration manager not configured.");

                // Mode 1: Entire configuration tree passed as { config: { directories, appearance, ... } }
                if (p.TryGetProperty("config", out var configProp))
                {
                    string fullConfigJson = configProp.GetRawText();
                    var newConfig = JsonSerializer.Deserialize<AppConfig>(fullConfigJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? throw new ArgumentException("Failed to deserialize configuration object.");

                    var updated = await _configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
                    EmitEvent("config.changed", new { category = "all", config = updated });
                    return new { ok = true, updatedConfig = updated };
                }

                // Mode 2: Per-category update passed as { category: "reader", settings: { ... } }
                string? cat = GetOptionalString(p, "category");
                if (!string.IsNullOrWhiteSpace(cat))
                {
                    string settingsJson;
                    if (p.TryGetProperty("settings", out var sProp))
                    {
                        settingsJson = sProp.ValueKind == JsonValueKind.String ? sProp.GetString()! : sProp.GetRawText();
                    }
                    else
                    {
                        settingsJson = p.GetRawText();
                    }

                    var updated = await _configManager.UpdateCategoryJsonAsync(cat, settingsJson, ct).ConfigureAwait(false);
                    EmitEvent("config.changed", new { category = cat, config = updated });
                    return new { ok = true, updatedConfig = updated };
                }

                // Mode 3: The parameters object itself is the AppConfig tree
                if (p.TryGetProperty("reader", out _) || p.TryGetProperty("appearance", out _))
                {
                    var newConfig = JsonSerializer.Deserialize<AppConfig>(p.GetRawText(), new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? throw new ArgumentException("Failed to deserialize configuration object.");

                    var updated = await _configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
                    EmitEvent("config.changed", new { category = "all", config = updated });
                    return new { ok = true, updatedConfig = updated };
                }

                throw new ArgumentException("Either 'config' or 'category' must be provided to 'config.update'.");
            }

            case "config.resetDefaults":
            {
                if (_configManager is null) throw new InvalidOperationException("Configuration manager not configured.");
                string? cat = GetOptionalString(p, "category");
                if (string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase))
                {
                    cat = null;
                }
                var updated = await _configManager.ResetDefaultsAsync(cat, ct).ConfigureAwait(false);
                EmitEvent("config.changed", new { category = cat ?? "all", config = updated });
                return new { ok = true, updatedConfig = updated };
            }

            case "config.testLlmEndpoint":
            {
                string endpoint = GetOptionalString(p, "endpointUrl") ?? "http://localhost:11434/v1/chat/completions";
                string apiKey = GetOptionalString(p, "apiKey") ?? "";
                string modelId = GetOptionalString(p, "modelId") ?? "llama3.2";
                int timeout = p.TryGetProperty("timeoutSeconds", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt32() : 5;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeout, 2, 30)) };
                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }

                var models = new List<string>();
                bool endpointReached = false;
                string statusMessage = "";

                try
                {
                    string modelsUrl = endpoint.Replace("/chat/completions", "/models");
                    if (modelsUrl.Contains("localhost:11434") && !modelsUrl.Contains("/v1/models"))
                    {
                        modelsUrl = "http://localhost:11434/api/tags";
                    }

                    var response = await client.GetAsync(modelsUrl, ct).ConfigureAwait(false);
                    sw.Stop();
                    if (response.IsSuccessStatusCode)
                    {
                        endpointReached = true;
                        statusMessage = $"Connected (HTTP {(int)response.StatusCode}) in {sw.ElapsedMilliseconds} ms";
                        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in dataArr.EnumerateArray())
                            {
                                if (el.TryGetProperty("id", out var idProp))
                                {
                                    string m = idProp.GetString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m);
                                }
                            }
                        }
                        else if (doc.RootElement.TryGetProperty("models", out var ollamaArr) && ollamaArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in ollamaArr.EnumerateArray())
                            {
                                if (el.TryGetProperty("name", out var nameProp))
                                {
                                    string m = nameProp.GetString() ?? "";
                                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m);
                                }
                            }
                        }
                    }
                    else
                    {
                        statusMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    statusMessage = $"Connection failed: {ex.Message}";
                }

                if (!string.IsNullOrWhiteSpace(modelId) && !models.Contains(modelId))
                {
                    models.Insert(0, modelId);
                }

                return new
                {
                    success = endpointReached,
                    latencyMs = sw.ElapsedMilliseconds,
                    statusMessage,
                    models,
                    activeModel = modelId
                };
            }

            case "extensions.list":
            {
                if (_featurePluginHost is null)
                {
                    return new { plugins = Array.Empty<object>(), uiPanels = Array.Empty<object>() };
                }
                return new
                {
                    plugins = _featurePluginHost.Plugins.Select(pl => new { pl.PluginId, pl.DisplayName, pl.Version }).ToArray(),
                    uiPanels = _featurePluginHost.UiPanels
                };
            }

            default:
            {
                if (_featurePluginHost is not null && _featurePluginHost.TryGetRpcHandler(method, out var extHandler) && extHandler is not null)
                {
                    return await extHandler(p, ct).ConfigureAwait(false);
                }
                throw new NotSupportedException($"Unknown RPC method: '{method}'.");
            }
        }
    }

    private async Task EnsureInitialStarterLibraryAsync(CancellationToken ct)
    {
        if (_bootstrappedWorkspaceBooks) return;
        _bootstrappedWorkspaceBooks = true;

        EnsureInitialStarterDictionaries(ct);

        var existing = _library.ListBooks(new BookListQuery(Limit: 1));
        var existingFolders = _library.GetFolderTree();
        if (existing.TotalCount > 0 || existingFolders.Count > 0) return;

        // Seed starter folder structure: Japanese -> Shounen / Seinen / Light Novels
        var jaRoot = _library.SaveFolder("fld-japanese", null, "Japanese", "collection", "Japanese Ebook Collection", sortOrder: 1);
        _library.SaveFolder("fld-shounen", jaRoot.FolderId, "Shounen", "category", sortOrder: 1);
        _library.SaveFolder("fld-seinen", jaRoot.FolderId, "Seinen", "category", sortOrder: 2);
        var lnFolder = _library.SaveFolder("fld-light-novels", jaRoot.FolderId, "Light Novels & Stories", "category", sortOrder: 3);

        if (!string.IsNullOrWhiteSpace(_workspaceBooksDir) && Directory.Exists(_workspaceBooksDir))
        {
            await _library.ScanDirectoryAsync(_workspaceBooksDir, targetFolderId: lnFolder.FolderId, recursive: true, ct: ct)
                .ConfigureAwait(false);
        }
    }

    private void EnsureInitialStarterDictionaries(CancellationToken ct)
    {
        if (_dictDb is null || string.IsNullOrWhiteSpace(_workspaceBooksDir)) return;

        try
        {
            long termCount = 0;
            long kanjiCount = 0;
            using (var conn = _dictDb.CreateConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM dictionary_sources;";
                    termCount = Convert.ToInt64(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM dictionary_kanji;";
                    kanjiCount = Convert.ToInt64(cmd.ExecuteScalar());
                }
            }

            if (termCount == 0 && Directory.Exists(_workspaceBooksDir))
            {
                var csvFiles = Directory.GetFiles(_workspaceBooksDir, "*.csv", SearchOption.AllDirectories);
                if (csvFiles.Length > 0)
                {
                    var importer = new YomitanSqliteImporter(_dictDb);
                    importer.ImportJmdictDeckCsvs(csvFiles);
                }
            }

            if (kanjiCount == 0)
            {
                string? extractorDir = Path.GetDirectoryName(_workspaceBooksDir);
                if (!string.IsNullOrEmpty(extractorDir))
                {
                    string dataDir = Path.Combine(extractorDir, "data");
                    string kanjidicGz = Path.Combine(dataDir, "kanjidic2.xml.gz");
                    string kaCsv = Path.Combine(dataDir, "kanji-alive", "ka_data.csv");
                    if (File.Exists(kanjidicGz))
                    {
                        var kImporter = new KanjidicImporter(_dictDb);
                        kImporter.Import(kanjidicGz, File.Exists(kaCsv) ? kaCsv : null, kanjivgZipPath: null, ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StarterDictionaries] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EmitEvent(string eventName, object data)
    {
        if (_sendMessageToUi is null) return;
        string json = JsonSerializer.Serialize(new { @event = eventName, data }, JsonOpts);
        _sendMessageToUi(json);
    }

    private static string GetRequiredString(JsonElement p, string propName)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            string? val = el.GetString();
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        throw new ArgumentException($"Missing required parameter '{propName}'.");
    }

    private static string? GetOptionalString(JsonElement p, string propName)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    private static List<string> GetStringList(JsonElement p, string propName)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }
        return [];
    }

    private static double? GetOptionalDouble(JsonElement p, string propName)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty(propName, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return d;
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pd)) return pd;
        }
        return null;
    }
}

