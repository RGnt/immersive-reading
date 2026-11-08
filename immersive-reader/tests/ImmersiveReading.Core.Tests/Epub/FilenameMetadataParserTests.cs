using ImmersiveReading.Core.Epub;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed class FilenameMetadataParserTests
{
    [Theory]
    [InlineData("[Author] Sample Title 第01巻 (電撃文庫).epub", "Sample Title 第01巻", "Author", "電撃文庫", "Sample Title", 1.0)]
    [InlineData("くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub", "くま クマ 熊 ベアー", "くまなの", "PASH! ブックス", "", null)]
    [InlineData("Spice and Wolf vol. 03.epub", "Spice and Wolf vol. 03", "", "", "Spice and Wolf", 3.0)]
    [InlineData("Sword Art Online v14.epub", "Sword Art Online v14", "", "", "Sword Art Online", 14.0)]
    [InlineData("幼女戦記 05.epub", "幼女戦記 05", "", "", "幼女戦記", 5.0)]
    [InlineData("無職転生 第12.5巻.epub", "無職転生 第12.5巻", "", "", "無職転生", 12.5)]
    [InlineData("", "", "", "", "", null)]
    public void Parse_ExtractsExpectedFields(
        string input,
        string expectedTitle,
        string expectedAuthor,
        string expectedImprint,
        string expectedSeries,
        double? expectedIndex)
    {
        var hints = FilenameMetadataParser.Parse(input);
        Assert.Equal(expectedTitle, hints.CleanTitle);
        Assert.Equal(expectedAuthor, hints.Authors);
        Assert.Equal(expectedImprint, hints.Imprint);
        Assert.Equal(expectedSeries, hints.SeriesName);
        Assert.Equal(expectedIndex, hints.SeriesIndex);
    }

    [Theory]
    [InlineData("9784048677608", true)] // Dengeki Bunko valid ISBN-13
    [InlineData("9784048677609", false)] // Invalid check digit
    [InlineData("1234567890123", false)] // Not starting with 978 or 979
    public void IsValidIsbn13_ValidatesCorrectly(string digits, bool expected)
    {
        bool actual = FilenameMetadataParser.IsValidIsbn13(digits);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0471958697", true)]
    [InlineData("020161622X", true)]
    [InlineData("0201616220", false)]
    public void IsValidIsbn10_ValidatesCorrectly(string digits, bool expected)
    {
        bool actual = FilenameMetadataParser.IsValidIsbn10(digits);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractIsbn_ExtractsFromSurroundingText()
    {
        string text = "Some Book Title [ISBN 978-4-04-867760-8] [Author].epub";
        string extracted = FilenameMetadataParser.ExtractIsbn(text);
        Assert.Equal("9784048677608", extracted);
    }

    [Fact]
    public void NormalizeFullWidthAscii_ConvertsCharactersProperly()
    {
        string input = "［Ａｕｔｈｏｒ］　Ｔｉｔｌｅ　第０１巻　（２０２０）";
        string normalized = FilenameMetadataParser.NormalizeFullWidthAscii(input);
        Assert.Contains("[Author]", normalized);
        Assert.Contains("Title", normalized);
        Assert.Contains("第01巻", normalized);
        Assert.Contains("(2020)", normalized);
    }
}
