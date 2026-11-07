using System.Text;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.AiCompanion;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using ImmersiveReading.Plugins.Japanese;
using Photino.NET;

namespace ImmersiveReading.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string readerRoot = FindReaderProjectRoot(AppContext.BaseDirectory);
        string workspaceRoot = FindWorkspaceRoot(readerRoot);
        string appDataDir = Path.Combine(readerRoot, ".library-data");
        Directory.CreateDirectory(appDataDir);

        string dbPath = Path.Combine(appDataDir, "library.db");
        string coversDir = Path.Combine(appDataDir, "covers");
        string extractorOutputDir = Path.Combine(workspaceRoot, "enrichement-extractor", "output");
        string extractorBooksDir = Path.Combine(workspaceRoot, "enrichement-extractor", "books");

        using var db = new LibraryDatabase(dbPath, coversDir);
        db.InitializeSchema();
        var libraryManager = new LibraryManager(db, extractorOutputDir);

        using var pluginRegistry = new LanguagePluginRegistry();
        var japanesePlugin = new JapaneseLanguagePlugin();
        pluginRegistry.Register(japanesePlugin);
        pluginRegistry.Register(new WhitespaceLanguagePlugin(), isFallback: true);

        var enrichmentCache = new ChapterEnrichmentCache(maxEntries: 30);
        var vocabTracker = new VocabularyTracker(db);
        var bookSchemeHandler = new BookSchemeHandler(
            libraryManager,
            pluginRegistry,
            enrichmentCache,
            getWordStatus: (lemma, reading) => vocabTracker.GetStatus("ja", lemma, reading));

        string dictDbPath = Path.Combine(appDataDir, "dictionaries.db");
        using var dictDb = new DictionaryDatabase(dictDbPath);
        var lookupEngine = new DictionaryLookupEngine(dictDb, enrichmentCache, vocabTracker);

        string extractorDir = Path.Combine(workspaceRoot, "enrichement-extractor");
        var studyPackBridge = new PythonStudyPackBridge(extractorOutputDir, extractorDir);
        var flashcardService = new FlashcardMiningService(extractorOutputDir);

        var readerContextService = new ReaderContextService(libraryManager, enrichmentCache, db, bookSchemeHandler);
        var snippetEnricher = new SnippetEnricher(
            pluginRegistry,
            enrichmentCache,
            getWordStatus: (lemma, reading) => vocabTracker.GetStatus("ja", lemma, reading));
        var vocabBudgetService = new VocabularyBudgetService(db, studyPackBridge);

        using var featurePluginHost = new FeaturePluginHost(readerContextService, vocabBudgetService, snippetEnricher);
        var aiCompanionPlugin = new AiCompanionPlugin(
            db,
            readerContextService,
            vocabBudgetService,
            snippetEnricher,
            japanesePlugin,
            new OpenAiCompatibleLlmProvider(new LlmEndpointOptions(EndpointUrl: "offline://heuristic", EnableOfflineHeuristicFallback: true)));
        featurePluginHost.RegisterPluginAsync(aiCompanionPlugin).GetAwaiter().GetResult();

        string wwwrootDir = ResolveWwwrootDir(AppContext.BaseDirectory, readerRoot);

        // Support headless CLI smoke verification for automated testing
        if (args.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            var smokeDispatcher = new LibraryRpcDispatcher(
                libraryManager,
                db,
                bookSchemeHandler,
                extractorBooksDir,
                lookupEngine: lookupEngine,
                vocabTracker: vocabTracker,
                dictDb: dictDb,
                studyPackBridge: studyPackBridge,
                flashcardService: flashcardService,
                featurePluginHost: featurePluginHost);
            string response = smokeDispatcher
                .DispatchJsonAsync("""{"id":"smoke-1","method":"library.getBootstrapState","params":{}}""")
                .GetAwaiter()
                .GetResult();
            Console.WriteLine(response);
            return 0;
        }

        if (args.Contains("--verify-reader", StringComparer.OrdinalIgnoreCase))
        {
            return RunReaderVerification(
                libraryManager,
                db,
                bookSchemeHandler,
                extractorBooksDir,
                lookupEngine,
                vocabTracker,
                dictDb,
                studyPackBridge,
                flashcardService,
                featurePluginHost,
                wwwrootDir,
                coversDir,
                extractorOutputDir);
        }

        bool verifyUi = args.Contains("--verify-ui", StringComparer.OrdinalIgnoreCase);
        int verifyExitCode = 0;

        PhotinoWindow? window = null;
        LibraryRpcDispatcher? dispatcher = null;
        dispatcher = new LibraryRpcDispatcher(
            libraryManager,
            db,
            bookSchemeHandler,
            extractorBooksDir,
            msg => window?.SendWebMessage(msg),
            lookupEngine: lookupEngine,
            vocabTracker: vocabTracker,
            dictDb: dictDb,
            studyPackBridge: studyPackBridge,
            flashcardService: flashcardService,
            featurePluginHost: featurePluginHost,
            onSelfTestCompleted: (passed, summary) =>
            {
                Console.WriteLine(summary);
                verifyExitCode = passed ? 0 : 1;
                window?.Close();
            });

        window = new PhotinoWindow()
            .SetTitle("Immersive Reading — EPUB Library & Reader")
            .SetUseOsDefaultSize(false)
            .SetSize(1480, 940)
            .Center()
            .RegisterCustomSchemeHandler("ir-app", (object sender, string scheme, string url, out string contentType) =>
            {
                return HandleAppSchemeRequest(url, wwwrootDir, coversDir, extractorOutputDir, out contentType);
            })
            .RegisterCustomSchemeHandler("ir-book", (object sender, string scheme, string url, out string contentType) =>
            {
                return bookSchemeHandler.Handle(url, out contentType);
            })
            .RegisterWebMessageReceivedHandler(async (object? sender, string message) =>
            {
                string reply = await dispatcher.DispatchJsonAsync(message).ConfigureAwait(false);
                window?.SendWebMessage(reply);

                if (verifyUi && message.Contains("\"library.getBootstrapState\"", StringComparison.Ordinal))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        window?.SendWebMessage("""{"event":"ui.runReaderSelfTest","data":{}}""");
                    });
                }
            });

        string indexHtmlPath = Path.Combine(wwwrootDir, "index.html");
        window.Load(indexHtmlPath);
        window.WaitForClose();
        return verifyExitCode;
    }

    public static Stream HandleAppSchemeRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        out string contentType)
        => HandleAppSchemeRequest(url, wwwrootDir, coversDir, extractorOutputDir: null, out contentType);

    public static Stream HandleAppSchemeRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir,
        out string contentType)
    {
        contentType = "text/plain; charset=utf-8";
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            string relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

            // Prevent path traversal
            if (relativePath.Contains("..", StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal))
            {
                return new MemoryStream([]);
            }

            if (host == "covers" || relativePath.StartsWith("covers/", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = host == "covers" ? Path.GetFileName(relativePath) : Path.GetFileName(relativePath["covers/".Length..]);
                string fullCoverPath = Path.Combine(coversDir, fileName);
                if (File.Exists(fullCoverPath))
                {
                    contentType = GetMimeType(fullCoverPath);
                    return new FileStream(fullCoverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            if (!string.IsNullOrEmpty(extractorOutputDir) &&
                (host == "studypack" || relativePath.StartsWith("studypack/", StringComparison.OrdinalIgnoreCase)))
            {
                string subPath = host == "studypack" ? relativePath : relativePath["studypack/".Length..];
                string fullStudyPath = Path.Combine(extractorOutputDir, subPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(fullStudyPath))
                {
                    contentType = GetMimeType(fullStudyPath);
                    return new FileStream(fullStudyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            // When URL is e.g. "ir-app://assets/ir-reader-injected.css", uri.Host is "assets" and uri.AbsolutePath is "/ir-reader-injected.css".
            if (!string.IsNullOrEmpty(host) && host != "app" && host != "localhost")
            {
                string hostCombined = Path.Combine(wwwrootDir, host, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(hostCombined))
                {
                    contentType = GetMimeType(hostCombined);
                    return new FileStream(hostCombined, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }

            string targetFile = Path.Combine(wwwrootDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(targetFile))
            {
                contentType = GetMimeType(targetFile);
                return new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }
        catch
        {
            // Fall through
        }

        return new MemoryStream(Encoding.UTF8.GetBytes("Not found"));
    }

    private static int RunReaderVerification(
        LibraryManager libraryManager,
        LibraryDatabase db,
        BookSchemeHandler bookSchemeHandler,
        string extractorBooksDir,
        DictionaryLookupEngine lookupEngine,
        VocabularyTracker vocabTracker,
        DictionaryDatabase dictDb,
        PythonStudyPackBridge studyPackBridge,
        FlashcardMiningService flashcardService,
        FeaturePluginHost featurePluginHost,
        string wwwrootDir,
        string coversDir,
        string extractorOutputDir)
    {
        var dispatcher = new LibraryRpcDispatcher(
            libraryManager,
            db,
            bookSchemeHandler,
            extractorBooksDir,
            lookupEngine: lookupEngine,
            vocabTracker: vocabTracker,
            dictDb: dictDb,
            studyPackBridge: studyPackBridge,
            flashcardService: flashcardService,
            featurePluginHost: featurePluginHost);

        // 1. Bootstrap library and dictionaries
        string bootstrapJson = dispatcher
            .DispatchJsonAsync("""{"id":"v-1","method":"library.getBootstrapState","params":{}}""")
            .GetAwaiter()
            .GetResult();

        // 2. Verify ir-app://assets/ir-reader-injected.css and ir-app://assets/ir-reader-bridge.js
        using var cssStream = HandleAppSchemeRequest("ir-app://assets/ir-reader-injected.css", wwwrootDir, coversDir, extractorOutputDir, out string cssMime);
        using var jsStream = HandleAppSchemeRequest("ir-app://assets/ir-reader-bridge.js", wwwrootDir, coversDir, extractorOutputDir, out string jsMime);
        if (cssStream.Length < 100 || !cssMime.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FAIL: ir-app://assets/ir-reader-injected.css failed (len={cssStream.Length}, mime={cssMime})");
            return 1;
        }
        if (jsStream.Length < 100 || !jsMime.StartsWith("application/javascript", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"FAIL: ir-app://assets/ir-reader-bridge.js failed (len={jsStream.Length}, mime={jsMime})");
            return 1;
        }

        // 3. Pick a book from the library and open it via reader.openBook
        var page = libraryManager.ListBooks(new ImmersiveReading.Core.Library.BookListQuery(Search: "クーク", Limit: 5));
        if (page.Items.Count == 0)
        {
            page = libraryManager.ListBooks(new ImmersiveReading.Core.Library.BookListQuery(Limit: 5));
        }
        if (page.Items.Count == 0)
        {
            Console.Error.WriteLine("FAIL: No books in library.");
            return 1;
        }

        string bookId = page.Items[0].BookId;
        string openJson = dispatcher
            .DispatchJsonAsync($$$"""{"id":"v-2","method":"reader.openBook","params":{"bookId":"{{{bookId}}}"}}""")
            .GetAwaiter()
            .GetResult();

        var detail = libraryManager.GetBookDetail(bookId)!;
        var textChapter = detail.Chapters.FirstOrDefault(c => c.CharCount > 50 || c.ChapterIndex == 1) ?? detail.Chapters[0];
        string chJson = dispatcher
            .DispatchJsonAsync($$$"""{"id":"v-3","method":"reader.getChapterContent","params":{"bookId":"{{{bookId}}}","spineHref":"{{{textChapter.SpineHref}}}"}}""")
            .GetAwaiter()
            .GetResult();

        // 4. Verify XML well-formedness on enriched chapter via XDocument.Parse
        var enriched = bookSchemeHandler.GetEnrichedChapter(bookId, textChapter.SpineHref);
        if (enriched is null || string.IsNullOrWhiteSpace(enriched.EnrichedHtml))
        {
            Console.Error.WriteLine("FAIL: Enriched chapter HTML was null or empty.");
            return 1;
        }

        System.Xml.Linq.XDocument.Parse(enriched.EnrichedHtml);

        // 5. Verify lookup.query on a kanji token in the chapter
        var kanjiToken = enriched.TokenMap.Tokens.FirstOrDefault(t => t.RubyUnits.Any(r => r.Reading != null))
            ?? enriched.TokenMap.Tokens[0];
        var lookupRes = lookupEngine
            .QueryAsync(new LookupQuery(bookId, textChapter.SpineHref, kanjiToken.TokenId))
            .GetAwaiter()
            .GetResult();
        if (lookupRes is null || lookupRes.Terms.Count == 0)
        {
            Console.Error.WriteLine("FAIL: Dictionary lookup returned null or 0 terms.");
            return 1;
        }

        // 6. Verify Phase 4 Study Pack Overview & 20-Word Vocabulary Sets on prebuilt study pack book-4e0541b184c4ace9
        var studyPack = studyPackBridge.GetBookStudyPack("book-4e0541b184c4ace9");
        if (!studyPack.Available || studyPack.MasterPdfs.Count == 0 || studyPack.VocabularySets.Count == 0)
        {
            Console.Error.WriteLine($"FAIL: Study pack overview missing PDFs or 20-word sets for book-4e0541b184c4ace9.");
            return 1;
        }

        Console.WriteLine($"VERIFY_READER_OK: book={bookId} ({detail.Summary.Title}), chapter={textChapter.SpineHref}, tokens={enriched.TokenMap.Tokens.Count}, lookupTop={lookupRes.Terms[0].Expression}[{lookupRes.Terms[0].Reading}], kanjiCards={lookupRes.Kanji.Count}, masterPdfs={studyPack.MasterPdfs.Count}, vocabSets={studyPack.VocabularySets.Count}");
        return 0;
    }

    private static string GetMimeType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }

    private static string FindReaderProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ImmersiveReading.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string FindWorkspaceRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "enrichement-extractor")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return startDir;
    }

    private static string ResolveWwwrootDir(string baseDir, string readerRoot)
    {
        string srcWww = Path.Combine(readerRoot, "src", "ImmersiveReading.App", "wwwroot");
        if (Directory.Exists(srcWww)) return srcWww;

        string localWww = Path.Combine(baseDir, "wwwroot");
        return localWww;
    }
}
