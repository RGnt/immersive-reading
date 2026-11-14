using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Enrichment.Dom;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.Japanese;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImmersiveReading.Enrichment.Tests;

/// <summary>Spec 12 FR-7..FR-9: chapters render as one merged document keyed by the primary href.</summary>
public sealed class ChapterGroupingRenderTests : IDisposable
{
    private const string BookId = "book-test";
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _library;
    private readonly ChapterEnrichmentCache _cache = new(maxEntries: 20);
    private readonly BookSchemeHandler _handler;
    private readonly LanguagePluginRegistry _plugins = new();

    public ChapterGroupingRenderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-grouping-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _library = new LibraryManager(_db);
        _plugins.Register(new JapaneseLanguagePlugin());
        _plugins.Register(new WhitespaceLanguagePlugin(), isFallback: true);
        _handler = new BookSchemeHandler(_library, _plugins, _cache);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* Windows file locks */ }
    }

    private static string Page(string body, string head = "") => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml"><head><meta charset="UTF-8"/><title>t</title>{head}</head><body>{body}</body></html>
        """;

    // ---- composer unit tests ----

    [Fact]
    public void Compose_KeepsMemberOrder_AndWrapsEachMemberInAPartSection()
    {
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page("<p>AAA</p>")),
            new("OEBPS/text/b.xhtml", Page("<p>BBB</p>")),
            new("OEBPS/text/c.xhtml", Page("<p>CCC</p>")),
        ]);

        int a = html.IndexOf("AAA", StringComparison.Ordinal);
        int b = html.IndexOf("BBB", StringComparison.Ordinal);
        int c = html.IndexOf("CCC", StringComparison.Ordinal);
        Assert.True(a >= 0 && a < b && b < c);
        Assert.Equal(3, CountOccurrences(html, "class=\"ir-spine-part"));
        Assert.Contains("data-ir-spine-href=\"OEBPS/text/b.xhtml\"", html);
    }

    [Fact]
    public void Compose_ResolvesRelativeUrlsAgainstEachMembersOwnPath()
    {
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page("<p><img src=\"../images/a.jpg\"/></p>")),
            new("OEBPS/deep/sub/b.xhtml", Page("<p><img src=\"../../images/b.jpg\"/></p>")),
        ]);

        Assert.Contains("ir-book://b1/OEBPS/images/a.jpg", html);
        Assert.Contains("ir-book://b1/OEBPS/images/b.jpg", html);
    }

    [Fact]
    public void Compose_DeduplicatesHeadStylesheets_AndAddsNewOnes()
    {
        string shared = "<link rel=\"stylesheet\" href=\"../css/shared.css\"/>";
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page("<p>A</p>", shared)),
            new("OEBPS/text/b.xhtml", Page("<p>B</p>", shared + "<link rel=\"stylesheet\" href=\"../css/only-b.css\"/>")),
        ]);

        Assert.Equal(1, CountOccurrences(html, "OEBPS/css/shared.css"));
        Assert.Equal(1, CountOccurrences(html, "OEBPS/css/only-b.css"));
    }

    [Fact]
    public void Compose_MarksImagesLazy_SoImageHeavyChaptersDoNotLoadEverythingAtOnce()
    {
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page("<p><img src=\"../images/a.jpg\"/></p>")),
            new("OEBPS/text/b.xhtml", Page("<p><img src=\"../images/b.jpg\" loading=\"eager\"/></p>")),
        ]);

        Assert.Equal(1, CountOccurrences(html, "loading=\"lazy\""));   // a.jpg set to lazy
        Assert.Contains("loading=\"eager\"", html);                   // an explicit author choice is kept
    }

    [Fact]
    public void Compose_CarriesBodyClassOntoThePartSection()
    {
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", "<html><head></head><body class=\"vrtl\"><p>A</p></body></html>"),
            new("OEBPS/text/b.xhtml", "<html><head></head><body class=\"plain\"><p>B</p></body></html>"),
        ]);

        Assert.Contains("class=\"ir-spine-part vrtl\"", html);
        Assert.Contains("class=\"ir-spine-part plain\"", html);
    }

    // ---- illustration parts (spec 14) ----

    [Fact]
    public void Compose_MarksIllustrationParts_ButNotTextParts()
    {
        string svgPage = "<div class=\"main\"><svg width=\"100%\" height=\"100%\" viewBox=\"0 0 716 1024\"><image width=\"716\" height=\"1024\" xlink:href=\"../images/a.jpg\"/></svg></div>";
        string imgPage = "<div><img src=\"../images/b.jpg\" alt=\"\"/></div>";
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page(svgPage)),
            new("OEBPS/text/b.xhtml", Page(imgPage)),
            new("OEBPS/text/c.xhtml", Page("<p>" + new string('あ', 120) + "</p>")),
            new("OEBPS/text/d.xhtml", Page("<p>" + new string('い', 120) + "</p><img src=\"../images/d.jpg\"/>")),
        ]);

        Assert.Equal(2, CountOccurrences(html, ChapterDocumentComposer.ImagePartClass));
        Assert.Contains($"class=\"ir-spine-part {ChapterDocumentComposer.ImagePartClass}\" data-ir-spine-href=\"OEBPS/text/a.xhtml\"", html);
        Assert.Contains($"class=\"ir-spine-part {ChapterDocumentComposer.ImagePartClass}\" data-ir-spine-href=\"OEBPS/text/b.xhtml\"", html);
        Assert.Contains("class=\"ir-spine-part\" data-ir-spine-href=\"OEBPS/text/c.xhtml\"", html);
        Assert.Contains("class=\"ir-spine-part\" data-ir-spine-href=\"OEBPS/text/d.xhtml\"", html);   // text with an inline picture stays in flow
    }

    [Fact]
    public void Compose_RubyAnnotationsDoNotMakeAnIllustrationPartATextPart()
    {
        string caption = "<p><ruby>漢<rt>" + new string('か', 80) + "</rt></ruby></p><img src=\"../images/a.jpg\"/>";
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", Page(caption)),
            new("OEBPS/text/b.xhtml", Page("<p>x</p>")),
        ]);

        Assert.Equal(1, CountOccurrences(html, ChapterDocumentComposer.ImagePartClass));
    }

    [Fact]
    public void Compose_KeepsTheBodyClassNextToTheIllustrationClass()
    {
        string html = ChapterDocumentComposer.Compose("b1",
        [
            new("OEBPS/text/a.xhtml", "<html><head></head><body class=\"fit\"><img src=\"../images/a.jpg\"/></body></html>"),
            new("OEBPS/text/b.xhtml", "<html><head></head><body><img src=\"../images/b.jpg\"/></body></html>"),
        ]);

        Assert.Contains($"class=\"ir-spine-part {ChapterDocumentComposer.ImagePartClass} fit\"", html);
    }

    // ---- scheme handler integration ----

    private async Task<string> ImportSplitBookAsync()
    {
        string path = Path.Combine(_tempDir, "split.epub");
        string text1 = "<p>吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。何でも薄暗いじめじめした所でニャーニャー泣いていた事だけは記憶している。</p>";
        string text2 = "<p>吾輩はここで始めて人間というものを見た。しかもあとで聞くとそれは書生という人間中で一番獰悪な種族であったそうだ。</p>";
        string illustration = "<div><img src=\"../images/pic.jpg\" alt=\"\"/></div>";
        string text3 = "<p>この書生というのは時々我々を捕えて煮て食うという話である。</p>";
        string chapter2 = "<p>第二章の本文である。猫は静かに眠った。それから何日かが過ぎて、春になった。</p>";

        using (var fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, level).Open(), new UTF8Encoding(false));
                w.Write(content);
            }

            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <?xml version="1.0"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                </container>
                """);
            Add("OEBPS/content.opf", """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                    <dc:title>Split Book</dc:title><dc:language>ja</dc:language><dc:identifier id="id">urn:uuid:split-book</dc:identifier>
                  </metadata>
                  <manifest>
                    <item id="nav" href="text/nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="c1a" href="text/c1_000.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c1b" href="text/c1_001.xhtml" media-type="application/xhtml+xml"/>
                    <item id="img" href="text/img1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c1c" href="text/c1_002.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="text/c2_000.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine><itemref idref="c1a"/><itemref idref="c1b"/><itemref idref="img"/><itemref idref="c1c"/><itemref idref="c2"/></spine>
                </package>
                """);
            Add("OEBPS/text/nav.xhtml", Page("""
                <nav xmlns:epub="http://www.idpf.org/2007/ops" epub:type="toc"><ol>
                  <li><a href="c1_000.xhtml">第一章</a></li>
                  <li><a href="c2_000.xhtml">第二章</a></li>
                </ol></nav>
                """));
            Add("OEBPS/text/c1_000.xhtml", Page(text1));
            Add("OEBPS/text/c1_001.xhtml", Page(text2));
            Add("OEBPS/text/img1.xhtml", Page(illustration));
            Add("OEBPS/text/c1_002.xhtml", Page(text3));
            Add("OEBPS/text/c2_000.xhtml", Page(chapter2));
        }

        var detail = await _library.ImportBookAsync(path);
        return detail.Summary.BookId;
    }

    private string Request(string bookId, string href)
    {
        using var stream = _handler.Handle($"ir-book://{bookId}/{href}", out _);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Handler_RendersAllMembersAsOneDocument_InSpineOrder_WithInlineIllustration()
    {
        string bookId = await ImportSplitBookAsync();
        var detail = _library.GetBookDetail(bookId)!;
        Assert.Equal(2, detail.Chapters.Count);
        Assert.Equal(4, detail.Chapters[0].Members.Count);

        string html = Request(bookId, detail.Chapters[0].SpineHref);

        // Paragraphs: 0 = c1_000, 1 = c1_001, (illustration has none), 2 = c1_002. Text is split into token spans,
        // so check order through paragraph ids and the token map rather than raw text.
        int p1 = html.IndexOf("data-ir-pid=\"1\"", StringComparison.Ordinal);
        int pic = html.IndexOf("images/pic.jpg", StringComparison.Ordinal);
        int p2 = html.IndexOf("data-ir-pid=\"2\"", StringComparison.Ordinal);
        Assert.True(p1 >= 0 && pic > p1 && p2 > pic, "the illustration must sit between the second and third text parts");
        Assert.Contains($"ir-book://{bookId}/OEBPS/images/pic.jpg", html);

        var texts = _cache.Get(bookId, detail.Chapters[0].SpineHref)!.ParagraphTexts;
        Assert.Contains("吾輩は猫である", texts[0]);
        Assert.Contains("人間というもの", texts[1]);
        Assert.Contains("煮て食う", texts[2]);
        Assert.DoesNotContain(texts, t => t.Contains("第二章"));
    }

    [Fact]
    public async Task Handler_ContinuationHref_ResolvesToTheOwningChapter_AndTokenMapIsKeyedByPrimary()
    {
        string bookId = await ImportSplitBookAsync();
        var chapter = _library.GetBookDetail(bookId)!.Chapters[0];
        string continuation = chapter.Members[1];

        string viaContinuation = Request(bookId, continuation);

        Assert.Contains("ir-tok", viaContinuation);
        var map = _cache.Get(bookId, chapter.SpineHref);
        Assert.NotNull(map);
        Assert.Contains(map.ParagraphTexts, t => t.Contains("吾輩は猫である"));      // the primary member's text
        Assert.Contains(map.ParagraphTexts, t => t.Contains("人間というもの"));      // and the continuation's
        Assert.Null(_cache.Get(bookId, continuation));
    }

    [Fact]
    public async Task Handler_TokenIdsAreContiguousAcrossMembers()
    {
        string bookId = await ImportSplitBookAsync();
        var chapter = _library.GetBookDetail(bookId)!.Chapters[0];
        Request(bookId, chapter.SpineHref);

        var map = _cache.Get(bookId, chapter.SpineHref)!;
        var ids = map.Tokens.Select(t => t.TokenId).ToList();
        Assert.True(ids.Count > 20);
        Assert.Equal(Enumerable.Range(0, ids.Count), ids);
        Assert.True(map.Tokens.Select(t => t.ParagraphId).Distinct().Count() >= 3);   // 3 text parts => 3+ paragraphs
    }

    [Fact]
    public async Task Handler_SingleFileChapter_IsIdenticalToDirectEnrichment()
    {
        string bookId = await ImportSplitBookAsync();
        var second = _library.GetBookDetail(bookId)!.Chapters[1];
        Assert.Single(second.Members);

        string viaHandler = Request(bookId, second.SpineHref);

        string raw = Page("<p>第二章の本文である。猫は静かに眠った。それから何日かが過ぎて、春になった。</p>");
        var direct = AngleSharpDomEnricher.EnrichChapter(raw, bookId, second.SpineHref, _plugins.Resolve("ja"), _handler.CurrentOptions);

        Assert.Equal(direct.EnrichedHtml, viaHandler);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }
}
