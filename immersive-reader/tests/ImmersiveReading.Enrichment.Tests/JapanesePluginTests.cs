using System.Text;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.Japanese;
using Xunit;

namespace ImmersiveReading.Enrichment.Tests;

public sealed class JapanesePluginTests
{
    [Theory]
    [InlineData("食べました", "食べる", "v1")]
    [InlineData("飲んだ", "飲む", "v5m")]
    [InlineData("行かない", "行く", "v5k")]
    [InlineData("美しかった", "美しい", "adj-i")]
    [InlineData("走らせる", "走る", "v5r")]
    public void JapaneseDeinflector_FindsExpectedBaseFormAndPos(string surface, string expectedBase, string expectedPos)
    {
        var deinflector = new JapaneseDeinflector();
        var candidates = deinflector.Deinflect(surface);

        Assert.NotEmpty(candidates);
        var match = candidates.FirstOrDefault(c => c.BaseForm == expectedBase && c.PartOfSpeechConstraint == expectedPos);
        Assert.NotNull(match.BaseForm);
    }

    [Fact]
    public void JapaneseDeinflector_BoundsCacheCapacity()
    {
        var deinflector = new JapaneseDeinflector();
        for (int i = 0; i < 12_000; i++)
        {
            deinflector.Deinflect($"単語{i}ました");
        }

        // Cache count should be bounded and not grow past 10,000 indefinitely
        Assert.True(deinflector.CacheCount <= 10_000);
    }

    [Fact]
    public void OkuriganaRubyAligner_AlignsOkuriganaCorrectly()
    {
        var aligner = new OkuriganaRubyAligner();
        var units = aligner.AlignRuby("食べる", "たべる", []);

        Assert.Equal(2, units.Count);
        Assert.Equal("食", units[0].BaseText);
        Assert.Equal("た", units[0].Reading);
        Assert.Equal("べる", units[1].BaseText);
        Assert.Null(units[1].Reading);
    }

    [Fact]
    public void KanaNormalizer_ConvertsKatakanaToHiragana()
    {
        string hiragana = KanaNormalizer.ToHiragana("スニーカー");
        Assert.Equal("すにーかー", hiragana);
    }

    [Theory]
    [InlineData('漢', true)]
    [InlineData('字', true)]
    [InlineData('あ', false)]
    [InlineData('ア', false)]
    [InlineData('A', false)]
    public void KanaNormalizer_IsKanji_IdentifiesIdeographs(char c, bool expected)
    {
        Assert.Equal(expected, KanaNormalizer.IsKanji(new Rune(c)));
    }
}

