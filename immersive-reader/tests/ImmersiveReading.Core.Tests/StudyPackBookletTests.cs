using ImmersiveReading.Library.StudyPack;

namespace ImmersiveReading.Core.Tests;

/// <summary>Spec 16: the grammar and particle booklets show up in the study pack drawer next to the kanji and vocabulary ones.</summary>
public sealed class StudyPackBookletTests : IDisposable
{
    private const string BookId = "book-0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ir-booklets-" + Guid.NewGuid().ToString("N"));

    public StudyPackBookletTests()
    {
        string pdf = Path.Combine(_root, "pdf", BookId);
        Directory.CreateDirectory(pdf);
        foreach (string name in new[]
        {
            "kanji-master.pdf", "vocabulary-master.pdf", "grammar-master.pdf", "function-words-master.pdf",
            "chapter-001-kanji.pdf", "chapter-001-vocabulary.pdf", "chapter-001-grammar.pdf", "chapter-001-function-words.pdf",
            "chapter-002-kanji.pdf", "chapter-002-vocabulary.pdf", "chapter-002-grammar.pdf"
        })
            File.WriteAllBytes(Path.Combine(pdf, name), [0x25, 0x50, 0x44, 0x46]);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    [Fact]
    public void Master_ListsTheFunctionWordsSheet()
    {
        var overview = StudyPackOverviewBuilder.GetBookStudyPack(_root, BookId);

        Assert.Equal(4, overview.MasterPdfs.Count);
        Assert.Contains(overview.MasterPdfs, p => p.FileName == "function-words-master.pdf" && p.Title.Contains("Particles"));
    }

    [Fact]
    public void ChapterBooklets_AreClassifiedByKind_AndAllListed()
    {
        var overview = StudyPackOverviewBuilder.GetBookStudyPack(_root, BookId);

        Assert.Equal(7, overview.ChapterPdfs.Count);
        Assert.Equal("chapter-grammar", overview.ChapterPdfs.Single(p => p.FileName == "chapter-001-grammar.pdf").Kind);
        Assert.Equal("chapter-function-words", overview.ChapterPdfs.Single(p => p.FileName == "chapter-001-function-words.pdf").Kind);
        Assert.Equal("chapter-kanji", overview.ChapterPdfs.Single(p => p.FileName == "chapter-002-kanji.pdf").Kind);
        Assert.Equal("chapter-vocabulary", overview.ChapterPdfs.Single(p => p.FileName == "chapter-002-vocabulary.pdf").Kind);
        Assert.DoesNotContain(overview.ChapterPdfs, p => p.FileName == "chapter-002-function-words.pdf");   // no empty booklets
    }

    [Theory]
    [InlineData("chapter-003-kanji.pdf", "chapter-kanji")]
    [InlineData("chapter-003-vocabulary.pdf", "chapter-vocabulary")]
    [InlineData("chapter-003-grammar.pdf", "chapter-grammar")]
    [InlineData("chapter-003-function-words.pdf", "chapter-function-words")]
    public void ChapterPdfKind_FollowsTheFileName(string file, string kind) => Assert.Equal(kind, StudyPackOverviewBuilder.ChapterPdfKind(file));
}
