using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

public sealed class JapaneseLanguagePlugin : ILanguagePlugin, IImmersivePlugin
{
    private readonly string? _dictionaryDir;
    private readonly JapaneseDeinflector _deinflector = new();

    public PluginManifest Manifest { get; } = new()
    {
        Id = "japanese",
        Name = "Japanese Language Support",
        Version = "1.0.0",
        Author = "Immersive Reading",
        Description = "Japanese morphological tokenizer (MeCab), ruby phonetics, and deinflection.",
        ReloadBehavior = PluginReloadBehavior.RestartRequired
    };

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

    public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        context.RegisterLanguageAnalyzer(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
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
