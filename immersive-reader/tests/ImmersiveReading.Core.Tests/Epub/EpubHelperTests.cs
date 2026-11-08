using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed class EpubHelperTests
{
    [Theory]
    [InlineData("表紙", true)]
    [InlineData("目次", true)]
    [InlineData("Cover", true)]
    [InlineData("Contents", true)]
    [InlineData("Preface", true)]
    [InlineData("第1話 旅立ち", false)]
    public void FrontMatterClassifier_IsFrontMatterTitle_IdentifiesCorrectly(string title, bool expected)
    {
        Assert.Equal(expected, FrontMatterClassifier.IsFrontMatterTitle(title));
    }

    [Theory]
    [InlineData("奥付", true)]
    [InlineData("あとがき", true)]
    [InlineData("Colophon", true)]
    [InlineData("Afterword", true)]
    [InlineData("第1話 旅立ち", false)]
    public void FrontMatterClassifier_IsBackMatterTitle_IdentifiesCorrectly(string title, bool expected)
    {
        Assert.Equal(expected, FrontMatterClassifier.IsBackMatterTitle(title));
    }

    [Fact]
    public void MetadataMerger_FallsBackToHints_WhenOpfFieldsMissing()
    {
        var rawOpf = new OpfOriginalMetadata(
            Title: "",
            SortTitle: "",
            Authors: "",
            AuthorSort: "",
            Isbn: "",
            Publisher: "Publisher X",
            Imprint: "",
            PublishedDate: "2024-01-01",
            Language: "ja",
            Description: "Desc",
            OpfIdentifier: "id-1",
            SeriesName: "",
            SeriesIndex: null,
            WritingMode: "auto"
        );

        var fileHints = new FilenameParsedHints(
            CleanTitle: "Book Title Vol.02",
            Authors: "File Author",
            Imprint: "File Imprint",
            SeriesName: "Book Title",
            SeriesIndex: 2.0,
            Isbn: "9784048677608"
        );

        var titleHints = new FilenameParsedHints("", "", "", "", null, "");

        var merged = MetadataMerger.Merge(rawOpf, fileHints, titleHints, "sample.epub");

        Assert.Equal("Book Title Vol.02", merged.Title);
        Assert.Equal("File Author", merged.Authors);
        Assert.Equal("File Imprint", merged.Imprint);
        Assert.Equal("Book Title", merged.SeriesName);
        Assert.Equal(2.0, merged.SeriesIndex);
        Assert.Equal("9784048677608", merged.Isbn);
    }
}

