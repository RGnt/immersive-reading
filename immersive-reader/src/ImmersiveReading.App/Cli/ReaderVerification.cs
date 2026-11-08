using System.Xml.Linq;
using ImmersiveReading.App.Composition;
using ImmersiveReading.App.Hosting;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Dictionaries;

namespace ImmersiveReading.App.Cli;

public static class ReaderVerification
{
    public static async Task<int> RunAsync(AppServices services, string[]? args = null)
    {
        string searchQuery = "クーク";
        string studyPackBookId = "book-4e0541b184c4ace9";

        if (args != null)
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("--verify-search=", StringComparison.OrdinalIgnoreCase))
                {
                    searchQuery = arg["--verify-search=".Length..];
                }
                else if (arg.StartsWith("--verify-book=", StringComparison.OrdinalIgnoreCase))
                {
                    studyPackBookId = arg["--verify-book=".Length..];
                }
            }
        }

        var dispatcher = new LibraryRpcDispatcher(services);

        // 1. Bootstrap library and dictionaries
        string bootstrapJson = await dispatcher
            .DispatchJsonAsync("""{"id":"v-1","method":"library.getBootstrapState","params":{}}""")
            .ConfigureAwait(false);

        // 2. Verify ir-app://assets/ir-reader-injected.css and ir-app://assets/ir-reader-bridge.js
        var schemeHandler = new AppSchemeHandler(services.Paths.WwwrootDir, services.Paths.CoversDir, services.Paths.ExtractorOutputDir);
        using var cssStream = schemeHandler.Handle("ir-app://assets/ir-reader-injected.css", out string cssMime);
        using var jsStream = schemeHandler.Handle("ir-app://assets/ir-reader-bridge.js", out string jsMime);
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
        var page = services.Library.ListBooks(new BookListQuery(Search: searchQuery, Limit: 5));
        if (page.Items.Count == 0)
        {
            page = services.Library.ListBooks(new BookListQuery(Limit: 5));
        }
        if (page.Items.Count == 0)
        {
            Console.Error.WriteLine("FAIL: No books in library.");
            return 1;
        }

        string bookId = page.Items[0].BookId;
        string openJson = await dispatcher
            .DispatchJsonAsync($$$"""{"id":"v-2","method":"reader.openBook","params":{"bookId":"{{{bookId}}}"}}""")
            .ConfigureAwait(false);

        var detail = services.Library.GetBookDetail(bookId)!;
        var textChapter = detail.Chapters.FirstOrDefault(c => c.CharCount > 50 || c.ChapterIndex == 1) ?? detail.Chapters[0];
        string chJson = await dispatcher
            .DispatchJsonAsync($$$"""{"id":"v-3","method":"reader.getChapterContent","params":{"bookId":"{{{bookId}}}","spineHref":"{{{textChapter.SpineHref}}}"}}""")
            .ConfigureAwait(false);

        // 4. Verify XML well-formedness on enriched chapter via XDocument.Parse
        var enriched = services.BookScheme.GetEnrichedChapter(bookId, textChapter.SpineHref);
        if (enriched is null || string.IsNullOrWhiteSpace(enriched.EnrichedHtml))
        {
            Console.Error.WriteLine("FAIL: Enriched chapter HTML was null or empty.");
            return 1;
        }

        XDocument.Parse(enriched.EnrichedHtml);

        // 5. Verify lookup.query on a kanji token in the chapter
        var kanjiToken = enriched.TokenMap.Tokens.FirstOrDefault(t => t.RubyUnits.Any(r => r.Reading != null))
            ?? enriched.TokenMap.Tokens[0];
        var lookupRes = await services.LookupEngine
            .QueryAsync(new LookupQuery(bookId, textChapter.SpineHref, kanjiToken.TokenId))
            .ConfigureAwait(false);
        if (lookupRes is null || lookupRes.Terms.Count == 0)
        {
            Console.Error.WriteLine("FAIL: Dictionary lookup returned null or 0 terms.");
            return 1;
        }

        // 6. Verify Phase 4 Study Pack Overview & 20-Word Vocabulary Sets
        var studyPack = services.StudyPackBridge.GetBookStudyPack(studyPackBookId);
        if (!studyPack.Available || studyPack.MasterPdfs.Count == 0 || studyPack.VocabularySets.Count == 0)
        {
            Console.Error.WriteLine($"FAIL: Study pack overview missing PDFs or 20-word sets for {studyPackBookId}.");
            return 1;
        }

        // 7. Verify config.update RPC with full config object and category reset
        string cfgUpdateRes = await dispatcher
            .DispatchJsonAsync("""{"id":"v-cfg-1","method":"config.update","params":{"config":{"reader":{"fontSizePx":26,"writingMode":"horizontal-tb"}}}}""")
            .ConfigureAwait(false);
        if (!cfgUpdateRes.Contains("\"ok\":true"))
        {
            Console.Error.WriteLine($"FAIL: config.update failed: {cfgUpdateRes}");
            return 1;
        }

        string cfgResetRes = await dispatcher
            .DispatchJsonAsync("""{"id":"v-cfg-2","method":"config.resetDefaults","params":{"category":"all"}}""")
            .ConfigureAwait(false);
        if (!cfgResetRes.Contains("\"ok\":true"))
        {
            Console.Error.WriteLine($"FAIL: config.resetDefaults failed: {cfgResetRes}");
            return 1;
        }

        Console.WriteLine($"VERIFY_READER_OK: book={bookId} ({detail.Summary.Title}), chapter={textChapter.SpineHref}, tokens={enriched.TokenMap.Tokens.Count}, lookupTop={lookupRes.Terms[0].Expression}[{lookupRes.Terms[0].Reading}], kanjiCards={lookupRes.Kanji.Count}, masterPdfs={studyPack.MasterPdfs.Count}, vocabSets={studyPack.VocabularySets.Count}");
        return 0;
    }
}

