using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.Japanese;

namespace ImmersiveReading.Enrichment.Tests;

public sealed class Phase2EnrichmentSpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _libraryManager;
    private readonly LanguagePluginRegistry _pluginRegistry;
    private readonly ChapterEnrichmentCache _cache;
    private readonly BookSchemeHandler _schemeHandler;

    public Phase2EnrichmentSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-phase2-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        string dbPath = Path.Combine(_tempDir, "library.db");
        _db = new LibraryDatabase(dbPath, Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();

        _libraryManager = new LibraryManager(_db);
        _pluginRegistry = new LanguagePluginRegistry();
        _pluginRegistry.Register(new JapaneseLanguagePlugin());
        _pluginRegistry.Register(new WhitespaceLanguagePlugin(), isFallback: true);

        _cache = new ChapterEnrichmentCache(maxEntries: 20);
        _schemeHandler = new BookSchemeHandler(_libraryManager, _pluginRegistry, _cache);
    }

    [Fact]
    public void EnrichChapter_WhenXhtmlContainsSelfClosingScriptAndStyle_CorrectlyTokenizesBodyText()
    {
        string rawXhtml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xml:lang="ja">
            <head>
              <meta charset="UTF-8"/>
              <title>Test Chapter</title>
              <script type="text/javascript" src="../js/kobo.js"/>
              <style type="text/css" id="koboStyle"/>
            </head>
            <body class="p-text">
              <div class="main">
                <p>草花香る緑の草原で魔女たちの茶会が開かれる。</p>
              </div>
            </body>
            </html>
            """;

        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawXhtml,
            "book-test",
            "text/ch1.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode: FuriganaMode.AllKanji));

        Assert.NotNull(result);
        Assert.NotEmpty(result.TokenMap.Tokens);
        Assert.Contains("ir-tok", result.EnrichedHtml);
        Assert.Contains("ir-furigana-all", result.EnrichedHtml);
        Assert.DoesNotContain("<script type=\"text/javascript\" src=\"../js/kobo.js\"/>", result.EnrichedHtml);
    }

    [Fact]
    public void EnrichChapter_FuriganaOffMode_SetsOffClass()
    {
        string rawXhtml = """
            <!DOCTYPE html>
            <html><head><title>Test</title></head>
            <body><p>日本語の文章です。</p></body></html>
            """;

        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawXhtml,
            "book-test",
            "text/ch1.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode: FuriganaMode.Off));

        Assert.Contains("ir-furigana-off", result.EnrichedHtml);
    }

    public void Dispose()
    {
        _pluginRegistry.Dispose();
        _db.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore lock on temp files
        }
    }

    [Theory]
    [InlineData("タベタ", "たべた")]
    [InlineData("フリカエル", "ふりかえる")]
    [InlineData("オトナ", "おとな")]
    public void KanaNormalizer_ToHiragana_ConvertsKatakana(string input, string expected)
    {
        Assert.Equal(expected, KanaNormalizer.ToHiragana(input));
    }

    [Fact]
    public void OkuriganaRubyAligner_AlignsKanjiStemsAndPlainOkurigana()
    {
        var aligner = new OkuriganaRubyAligner();

        // 1. 食べた (たべた) -> [Ruby("食", "た"), Plain("べた")]
        var units1 = aligner.AlignRuby("食べた", "たべた", []);
        Assert.Equal(2, units1.Count);
        Assert.Equal("食", units1[0].BaseText);
        Assert.Equal("た", units1[0].Reading);
        Assert.Equal(RubySource.PluginGenerated, units1[0].Source);
        Assert.Equal("べた", units1[1].BaseText);
        Assert.Null(units1[1].Reading);

        // 2. 振り返る (ふりかえる) -> [Ruby("振", "ふ"), Plain("り"), Ruby("返", "かえ"), Plain("る")]
        var units2 = aligner.AlignRuby("振り返る", "ふりかえる", []);
        Assert.Equal(4, units2.Count);
        Assert.Equal("振", units2[0].BaseText);
        Assert.Equal("ふ", units2[0].Reading);
        Assert.Equal("り", units2[1].BaseText);
        Assert.Null(units2[1].Reading);
        Assert.Equal("返", units2[2].BaseText);
        Assert.Equal("かえ", units2[2].Reading);
        Assert.Equal("る", units2[3].BaseText);
        Assert.Null(units2[3].Reading);

        // 3. 大人 (おとな) -> [Ruby("大人", "おとな")] (whole-word ruby)
        var units3 = aligner.AlignRuby("大人", "おとな", []);
        Assert.Single(units3);
        Assert.Equal("大人", units3[0].BaseText);
        Assert.Equal("おとな", units3[0].Reading);
    }

    [Fact]
    public void MeCabTokenizer_PreservesAuthorRubyReadingOverDefaultReading()
    {
        using var tokenizer = new MeCabTokenizerAdapter();
        string paragraph = "人気のない森で、木の実を食べた。";
        // Author explicitly assigned reading "ひとけ" to "人気"
        var authorRubies = new List<SourceRubySpan>
        {
            new SourceRubySpan(0, 2, "人気", "ひとけ")
        };

        var tokens = tokenizer.TokenizeParagraph(paragraph, authorRubies);
        var hitokeToken = tokens.FirstOrDefault(t => t.Surface == "人気");

        Assert.NotNull(hitokeToken.Surface);
        Assert.Equal("ひとけ", hitokeToken.Reading);
        Assert.Equal(RubySource.EpubAuthor, hitokeToken.ReadingSource);

        var tabetaToken = tokens.FirstOrDefault(t => t.Surface == "食べた");
        Assert.NotNull(tabetaToken.Surface);
        Assert.Equal("食べる", tabetaToken.Lemma);
        Assert.Equal("たべた", tabetaToken.Reading);
    }

    [Fact]
    public void AngleSharpDomEnricher_PreservesAuthorRubyAndInjectsOkuriganaRuby()
    {
        string rawHtml = """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Test Chapter</title></head>
              <body>
                <p><ruby>人気<rt>ひとけ</rt></ruby>のない森で、木の実を食べた。</p>
              </body>
            </html>
            """;

        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawHtml,
            "book-test",
            "text/ch01.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode.AllKanji));

        string enriched = result.EnrichedHtml;

        // 1. Verify head assets injected
        Assert.Contains("ir-app://assets/ir-reader-injected.css", enriched);
        Assert.Contains("ir-app://assets/ir-reader-bridge.js", enriched);

        // 2. Verify author ruby preserved
        Assert.Contains("<ruby class=\"ir-tok", enriched);
        Assert.Contains("人気<rt>ひとけ</rt>", enriched);

        // 3. Verify generated okurigana-aligned ruby injected for 食べた
        Assert.Contains("<ruby class=\"ir-ruby-gen\">食<rt>た</rt></ruby>べた", enriched);

        // 4. Verify token map has data-tid entries
        Assert.True(result.TokenMap.Tokens.Count > 0);
    }

    [Fact]
    public void AngleSharpDomEnricher_SkipsSvgAndScriptSubtrees()
    {
        string rawHtml = """
            <html>
              <body>
                <svg><text>表紙</text></svg>
                <script>console.log("no token");</script>
                <p>森を走る。</p>
              </body>
            </html>
            """;

        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawHtml,
            "book-test",
            "text/ch01.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode.AllKanji));

        string enriched = result.EnrichedHtml;
        Assert.Contains("<svg><text>表紙</text></svg>", enriched);
        Assert.DoesNotContain("<span class=\"ir-tok\" data-tid=\"0\"><ruby class=\"ir-ruby-gen\">表<rt>", enriched);
        Assert.Contains("data-ir-pid=\"0\"", enriched);
    }

    [Fact]
    public void AngleSharpDomEnricher_MultiNodeTokenSlices_ShareSameDataTid()
    {
        string rawHtml = "<html><body><p><em>人</em>気がある。</p></body></html>";
        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawHtml,
            "book-test",
            "text/ch01.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode.AllKanji));

        string enriched = result.EnrichedHtml;
        // The token for "人気" is split across <em> and text: both parts should exist and <em> must remain valid HTML
        Assert.Contains("<em><span class=\"ir-tok", enriched);
        Assert.Contains("data-part=\"0\"", enriched);
    }

    [Fact]
    public async Task BookSchemeHandler_EnrichesChapterAndBlocksPathTraversal()
    {
        // 1. Create a synthetic test EPUB
        string epubPath = CreateTestEpubWithImage("book_read_test.epub");
        var imported = await _libraryManager.ImportBookAsync(epubPath);

        // 2. Request chapter over ir-book://
        string chapterUrl = $"ir-book://{imported.Summary.BookId}/OEBPS/text/ch01.xhtml";
        using var stream = _schemeHandler.Handle(chapterUrl, out string contentType);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string content = reader.ReadToEnd();

        Assert.Equal("application/xhtml+xml; charset=utf-8", contentType);
        Assert.Contains("ir-app://assets/ir-reader-injected.css", content);
        Assert.Contains("ir-tok", content);

        // Verify chapter token map is cached
        var cachedMap = _cache.Get(imported.Summary.BookId, "OEBPS/text/ch01.xhtml");
        Assert.NotNull(cachedMap);

        // 3. Request relative image
        string imgUrl = $"ir-book://{imported.Summary.BookId}/OEBPS/images/cover.jpg";
        using var imgStream = _schemeHandler.Handle(imgUrl, out string imgContentType);
        Assert.Equal("image/jpeg", imgContentType);
        Assert.True(imgStream.Length > 0);

        // 4. Verify path traversal attempt is blocked
        string traversalUrl = $"ir-book://{imported.Summary.BookId}/../../Windows/win.ini";
        using var badStream = _schemeHandler.Handle(traversalUrl, out string _);
        Assert.Equal(0, badStream.Length);
    }

    [Fact]
    public void AngleSharpDomEnricher_RewritesRelativeUrlsAndProducesValidXml()
    {
        string rawHtml = """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:xlink="http://www.w3.org/1999/xlink">
              <head>
                <meta charset="utf-8"/>
                <link rel="stylesheet" type="text/css" href="../Styles/flow0001.css"/>
                <title>Cover &amp; Illustration</title>
              </head>
              <body>
                <div>
                  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 1600">
                    <image width="1200" height="1600" xlink:href="../Images/cover_hd.jpg"/>
                  </svg>
                </div>
                <p><img src="../Images/illust01.png" alt="Illustration"/><br/>森を走る。</p>
                <p><a href="ch02.xhtml#sec1">次へ</a></p>
              </body>
            </html>
            """;

        var plugin = _pluginRegistry.Resolve("ja");
        var result = AngleSharpDomEnricher.EnrichChapter(
            rawHtml,
            "book-123",
            "OEBPS/Text/ch01.xhtml",
            plugin,
            new EnrichmentOptions(FuriganaMode.AllKanji));

        // 1. Must parse cleanly as strict XML (application/xhtml+xml)
        var xdoc = System.Xml.Linq.XDocument.Parse(result.EnrichedHtml);
        Assert.NotNull(xdoc.Root);

        // 2. Verify relative URLs are rewritten to ir-book://book-123/OEBPS/...
        Assert.Contains("ir-book://book-123/OEBPS/Styles/flow0001.css", result.EnrichedHtml);
        Assert.Contains("ir-book://book-123/OEBPS/Images/cover_hd.jpg", result.EnrichedHtml);
        Assert.Contains("ir-book://book-123/OEBPS/Images/illust01.png", result.EnrichedHtml);
        Assert.Contains("data-ir-spine-href=\"OEBPS/Text/ch02.xhtml\"", result.EnrichedHtml);

        // 3. Verify CSS relative url(...) rewriting
        string css = "@font-face { src: url('../Fonts/mincho.woff2'); } body { background: url(\"../Images/bg.jpg\"); }";
        string rewrittenCss = BookSchemeHandler.RewriteCssRelativeUrls(css, "book-123", "OEBPS/Styles/flow0001.css");
        Assert.Contains("url(\"ir-book://book-123/OEBPS/Fonts/mincho.woff2\")", rewrittenCss);
        Assert.Contains("url(\"ir-book://book-123/OEBPS/Images/bg.jpg\")", rewrittenCss);
    }

    private string CreateTestEpubWithImage(string fileName)
    {
        string fullPath = Path.Combine(_tempDir, fileName);
        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteZip(zip, "mimetype", "application/epub+zip");
        WriteZip(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        WriteZip(zip, "OEBPS/content.opf", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Reader Test Book</dc:title>
                <dc:language>ja</dc:language>
                <dc:identifier id="id">urn:uuid:test</dc:identifier>
              </metadata>
              <manifest>
                <item id="ch1" href="text/ch01.xhtml" media-type="application/xhtml+xml"/>
                <item id="img1" href="images/cover.jpg" media-type="image/jpeg"/>
              </manifest>
              <spine>
                <itemref idref="ch1"/>
              </spine>
            </package>
            """);

        WriteZip(zip, "OEBPS/text/ch01.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>Chapter 1</title></head>
              <body>
                <p><ruby>人気<rt>ひとけ</rt></ruby>のない森で木の実を食べた。</p>
              </body>
            </html>
            """);

        var imgEntry = zip.CreateEntry("OEBPS/images/cover.jpg");
        using var s = imgEntry.Open();
        s.Write([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46]); // JPEG header bytes

        return fullPath;
    }

    private static void WriteZip(ZipArchive zip, string entryPath, string text)
    {
        var entry = zip.CreateEntry(entryPath);
        using var s = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }
}
