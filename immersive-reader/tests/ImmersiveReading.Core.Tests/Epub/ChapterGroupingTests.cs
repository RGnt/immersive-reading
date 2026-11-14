using System.Text;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

/// <summary>Spec 12: one chapter per ToC entry; split files and illustration pages are members.</summary>
public sealed class ChapterGroupingTests
{
    private static GroupingPage Text(string href, string? toc = null) => GroupingEpubFactory.Text(href, toc);
    private static GroupingPage Illustration(string href) => GroupingEpubFactory.Illustration(href);
    private static ExtractedEpubPackage Extract(IReadOnlyList<GroupingPage> spine, bool deepStats = false, bool withToc = true) =>
        GroupingEpubFactory.Extract(spine, deepStats, withToc);

    [Fact]
    public void SplitFilesOfOneTocEntry_BecomeOneChapterWithMembers()
    {
        var pkg = Extract([
            Text("text/p1_split_000.xhtml", "第1話"),
            Text("text/p1_split_001.xhtml"),
            Text("text/p2_split_000.xhtml", "第2話"),
            Text("text/p2_split_001.xhtml"),
        ]);

        Assert.Equal(2, pkg.Chapters.Count);
        var first = pkg.Chapters[0];
        Assert.Equal("第1話", first.Title);
        Assert.Equal("chapter-01", first.ChapterId);
        Assert.Equal(["text/p1_split_000.xhtml", "text/p1_split_001.xhtml"], first.Members.Select(NormalizeHref));
        Assert.EndsWith("p1_split_001.xhtml", first.EndSpineHref);
        Assert.Equal("chapter-02", pkg.Chapters[1].ChapterId);
        Assert.DoesNotContain(pkg.Chapters, c => c.Title.Contains("(Part") || c.Title.Contains("(Illustration"));
    }

    [Fact]
    public void IllustrationPagesBetweenTextParts_StayInsideTheChapter()
    {
        var pkg = Extract([
            Text("text/c1.xhtml", "第1話"),
            Illustration("text/img01.xhtml"),
            Text("text/c1b.xhtml"),
            Illustration("text/img02.xhtml"),
            Text("text/c2.xhtml", "第2話"),
        ]);

        Assert.Equal(2, pkg.Chapters.Count);
        Assert.Equal(4, pkg.Chapters[0].Members.Count);
        Assert.Single(pkg.Chapters[1].Members);
        Assert.False(pkg.Chapters[0].IsExcluded);
    }

    [Fact]
    public void ChapterWithEveryFileInTheToc_IsUnchanged()
    {
        var pkg = Extract([Text("text/a.xhtml", "第1話"), Text("text/b.xhtml", "第2話"), Text("text/c.xhtml", "第3話")]);

        Assert.Equal(3, pkg.Chapters.Count);
        Assert.All(pkg.Chapters, c =>
        {
            Assert.Single(c.Members);
            Assert.Null(c.EndSpineHref);
        });
        Assert.Equal(["chapter-01", "chapter-02", "chapter-03"], pkg.Chapters.Select(c => c.ChapterId));
    }

    [Fact]
    public void CharCountsSumOverMembers_AndCumulativeCharsStayConsistent()
    {
        var pkg = Extract([
            Text("text/a0.xhtml", "第1話"),
            Text("text/a1.xhtml"),
            Text("text/b0.xhtml", "第2話"),
        ], deepStats: true);

        var one = pkg.Chapters[0];
        var two = pkg.Chapters[1];
        Assert.True(one.CharCount > 0);
        Assert.Equal(one.CharCount, two.CharCount * 2);   // two members of equal text vs one
        Assert.Equal(0, one.CumulativeChars);
        Assert.Equal(one.CharCount, two.CumulativeChars);
        Assert.Equal(one.CharCount + two.CharCount, pkg.TotalCharCount);
    }

    [Fact]
    public void FrontMatter_RunsOfIllustrationPagesCollapse_AndStayExcluded()
    {
        var pkg = Extract([
            Illustration("text/p-cover.xhtml") with { TocTitle = "表紙" },
            Illustration("text/illust1.xhtml"),
            Illustration("text/illust2.xhtml"),
            Illustration("text/illust3.xhtml"),
            Text("text/toc-001.xhtml", "目次"),
            Text("text/c1.xhtml", "第1話"),
        ]);

        var front = pkg.Chapters.TakeWhile(c => c.IsExcluded).ToList();
        Assert.True(front.Count <= 3, $"expected collapsed front matter, got {front.Count}: {string.Join(", ", front.Select(f => f.Title))}");
        Assert.Contains(front, c => c.Members.Count == 3);   // the three illustration pages became one record

        var firstStory = pkg.Chapters.First(c => !c.IsExcluded);
        Assert.Equal("chapter-01", firstStory.ChapterId);
        Assert.Equal("第1話", firstStory.Title);
    }

    [Fact]
    public void FrontMatterTextPagesJoinTheirTocSection()
    {
        var pkg = Extract([
            Text("text/intro0.xhtml", "はじめに"),
            Text("text/intro1.xhtml"),
            Text("text/intro2.xhtml"),
            Text("text/c1.xhtml", "第1話"),
        ]);

        var intro = pkg.Chapters[0];
        Assert.True(intro.IsExcluded);
        Assert.Equal(3, intro.Members.Count);
        Assert.Equal("chapter-01", pkg.Chapters[1].ChapterId);
    }

    [Fact]
    public void BookWithoutToc_EachTextFileIsAChapter_AndIllustrationsJoinThePreviousOne()
    {
        var pkg = Extract([
            Text("text/a.xhtml"),
            Illustration("text/img1.xhtml"),
            Text("text/b.xhtml"),
            Text("text/c.xhtml"),
        ], withToc: false);

        var story = pkg.Chapters.Where(c => !c.IsExcluded).ToList();
        Assert.Equal(3, story.Count);
        Assert.Equal(["Chapter 1", "Chapter 2", "Chapter 3"], story.Select(c => c.Title));
        Assert.Equal(2, story[0].Members.Count);   // text + illustration
    }

    [Fact]
    public void BackMatter_FollowingFilesJoinOneBackRecord_AndAreExcluded()
    {
        var pkg = Extract([
            Text("text/c1.xhtml", "第1話"),
            Text("text/afterword.xhtml", "あとがき"),
            Illustration("text/ad1.xhtml"),
            Illustration("text/ad2.xhtml"),
        ]);

        Assert.Equal(2, pkg.Chapters.Count);
        var back = pkg.Chapters[1];
        Assert.True(back.IsExcluded);
        Assert.Equal(3, back.Members.Count);
        Assert.StartsWith("back-", back.ChapterId);
    }

    [Fact]
    public void BookChapterRecord_MembersFallsBackToPrimaryForLegacyRows()
    {
        var legacy = new BookChapterRecord("b", 0, "chapter-01", "T", "text/a.xhtml");
        Assert.Equal(["text/a.xhtml"], legacy.Members);
        Assert.True(legacy.OwnsSpineHref("TEXT/A.XHTML"));
        Assert.False(legacy.OwnsSpineHref("text/b.xhtml"));
    }

    // The OPF lives in OEBPS/, spine hrefs come back normalized relative to the archive root.
    private static string NormalizeHref(string href)
    {
        int i = href.IndexOf("text/", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? href[i..] : href;
    }
}
