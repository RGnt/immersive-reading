using System.IO.Compression;
using ImmersiveReading.Core.Epub;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

/// <summary>Spec 13: image-only detection (≥90% image pages AND &lt;80 visible chars per document).</summary>
public sealed class BookContentProfileTests
{
    private static GroupingPage Pic(int i) =>
        new($"pic{i}", $"text/pic{i:D3}.xhtml", "<div><img src=\"../images/p.jpg\" alt=\"\"/></div>");

    private static GroupingPage Prose(int i, int chars) =>
        new($"txt{i}", $"text/txt{i:D3}.xhtml", $"<p>{new string('あ', chars)}</p>");

    private static BookContentProfile ProfileOf(IEnumerable<GroupingPage> pages, string extraMetadata = "") =>
        GroupingEpubFactory.Extract(pages.ToList(), withToc: false, extraMetadata: extraMetadata).Content!;

    [Fact]
    public void AllImagePages_IsImageOnly()
    {
        var profile = ProfileOf(Enumerable.Range(0, 100).Select(Pic));

        Assert.Equal(100, profile.TotalDocuments);
        Assert.Equal(100, profile.ImageDocuments);
        Assert.True(profile.IsImageOnly);
    }

    [Fact]
    public void SingleImagePage_IsImageOnly_BecauseThereIsNothingToRead()
    {
        Assert.True(ProfileOf([Pic(0)]).IsImageOnly);
    }

    [Fact]
    public void LightNovelWithIllustrations_IsNotImageOnly()
    {
        var pages = Enumerable.Range(0, 30).Select(i => Prose(i, 2500)).Concat(Enumerable.Range(0, 8).Select(Pic));

        var profile = ProfileOf(pages);

        Assert.False(profile.IsImageOnly);
        Assert.True(profile.ImageDocumentRatio < 0.3);
    }

    [Fact]
    public void ImageRatioBoundary_NinetyPercentIsFlagged_EightyPercentIsNot()
    {
        // 9 image pages + 1 short text page = 90%, tiny average text
        Assert.True(ProfileOf(Enumerable.Range(0, 9).Select(Pic).Append(Prose(0, 100))).IsImageOnly);
        // 8 image pages + 2 short text pages = 80%
        Assert.False(ProfileOf(Enumerable.Range(0, 8).Select(Pic).Concat(new[] { Prose(0, 100), Prose(1, 100) })).IsImageOnly);
    }

    [Fact]
    public void CharsPerDocumentBoundary_SeventyIsFlagged_OneHundredIsNot()
    {
        // 18 image pages + 2 text pages: ratio 90%. Average chars = (2 * N) / 20.
        var images = Enumerable.Range(0, 18).Select(Pic).ToList();
        Assert.True(ProfileOf(images.Append(Prose(0, 700)).Append(Prose(1, 700))).IsImageOnly);     // avg 70
        Assert.False(ProfileOf(images.Append(Prose(0, 1000)).Append(Prose(1, 1000))).IsImageOnly);  // avg 100
    }

    [Fact]
    public void ManyImagePages_WithRealTextElsewhere_StaysImportable()
    {
        // The measured counter-example: 97% image pages but ~170 chars per document.
        var images = Enumerable.Range(0, 97).Select(Pic);
        var text = Enumerable.Range(0, 3).Select(i => Prose(i, 5700));

        Assert.False(ProfileOf(images.Concat(text)).IsImageOnly);
    }

    [Fact]
    public void DeclaredFixedLayout_AloneDoesNotFlagABookThatHasText()
    {
        const string fixedLayout = "<meta property=\"rendition:layout\">pre-paginated</meta>";
        var profile = ProfileOf(Enumerable.Range(0, 20).Select(i => Prose(i, 3000)), fixedLayout);

        Assert.True(profile.DeclaresFixedLayout);
        Assert.False(profile.IsImageOnly);
    }

    [Fact]
    public void DeclaredFixedLayoutWithOnlyImages_IsFlagged_AndSaysSo()
    {
        const string fixedLayout = "<meta property=\"rendition:layout\">pre-paginated</meta>";
        var profile = ProfileOf(Enumerable.Range(0, 40).Select(Pic), fixedLayout);

        Assert.True(profile.IsImageOnly);
        Assert.Contains("fixed layout", profile.Describe());
    }

    [Theory]
    [InlineData("<meta name=\"fixed-layout\" content=\"true\"/>")]
    [InlineData("<meta name=\"original-resolution\" content=\"1200x1600\"/>")]
    [InlineData("<meta name=\"book-type\" content=\"comic\"/>")]
    public void OtherFixedLayoutDeclarations_AreRecognised(string meta)
    {
        Assert.True(ProfileOf([Prose(0, 500)], meta).DeclaresFixedLayout);
    }

    [Fact]
    public void RubyReadings_AreNotCountedAsText()
    {
        Assert.Equal(2, BookContentAnalyzer.CountVisibleChars("<p><ruby>漢<rt>かん</rt></ruby><ruby>字<rp>(</rp><rt>じ</rt><rp>)</rp></ruby></p>"));
        Assert.Equal(3, BookContentAnalyzer.CountVisibleChars("<style>p{color:red}</style><script>var a=1;</script><p> あ い　う </p>"));
    }

    [Fact]
    public void SpineEntriesMissingFromTheArchive_AreIgnored_NotErrors()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("present.xhtml").Open());
            w.Write("<p>" + new string('あ', 400) + "</p>");
        }
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var profile = BookContentAnalyzer.Analyze(archive, ["missing.xhtml", "present.xhtml", "also-missing.xhtml"], "");

        Assert.Equal(1, profile.TotalDocuments);
        Assert.False(profile.IsImageOnly);

        var none = BookContentAnalyzer.Analyze(archive, ["missing.xhtml"], "");
        Assert.Equal(0, none.TotalDocuments);
        Assert.False(none.IsImageOnly);   // nothing to judge is never "image-only"
    }

    [Fact]
    public void ExceptionMessage_NamesTheFile_TheNumbers_AndTheReason()
    {
        var profile = ProfileOf(Enumerable.Range(0, 50).Select(Pic));
        var ex = new UnsupportedBookException("manga.epub", profile);

        Assert.Contains("manga.epub", ex.Message);
        Assert.Contains("50 of 50 pages are images", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
        Assert.Contains(".cbz", ex.Message);
    }
}
