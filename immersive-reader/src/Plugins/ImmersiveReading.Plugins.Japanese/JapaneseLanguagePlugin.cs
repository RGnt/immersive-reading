using System.Text;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

public sealed class JapaneseLanguagePlugin : ILanguagePlugin
{
    private readonly string? _dictionaryDir;

    private readonly JapaneseDeinflector _deinflector = new();

    public string LanguageCode => LanguageCodes.Japanese;
    public string DisplayName => "Japanese (MeCab.DotNet + Okurigana Aligner)";
    public LanguageCapabilities Capabilities =>
        LanguageCapabilities.MorphologicalTokenizer |
        LanguageCapabilities.RubyPhonetics |
        LanguageCapabilities.Deinflection |
        LanguageCapabilities.CharacterDecomposition |
        LanguageCapabilities.VerticalTypography;

    public JapaneseLanguagePlugin(string? dictionaryDir = null)
    {
        _dictionaryDir = dictionaryDir;
    }

    public ITextTokenizer CreateTokenizer() => new MeCabTokenizerAdapter(_dictionaryDir);

    public IDeinflector? GetDeinflector() => _deinflector;

    public IRubyAligner? GetRubyAligner() => new OkuriganaRubyAligner();

    public string NormalizeReading(ReadOnlySpan<char> reading) => KanaNormalizer.ToHiragana(reading);

    public bool NeedsPhoneticAnnotation(Rune character) => KanaNormalizer.IsKanji(character);

    public void Dispose()
    {
    }
}
