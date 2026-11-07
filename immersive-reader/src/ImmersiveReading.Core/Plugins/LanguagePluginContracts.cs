using System.Text;

namespace ImmersiveReading.Core.Plugins;

[Flags]
public enum LanguageCapabilities
{
    None                    = 0,
    MorphologicalTokenizer  = 1 << 0,
    Deinflection            = 1 << 1,
    RubyPhonetics           = 1 << 2,
    CharacterDecomposition  = 1 << 3,
    PitchOrToneAccent       = 1 << 4,
    VerticalTypography      = 1 << 5
}

public enum RubySource : byte
{
    None = 0,
    EpubAuthor = 1,
    PluginGenerated = 2
}

public enum WordStatus : byte
{
    New = 0,
    Learning = 1,
    Known = 2,
    Ignored = 3
}

public enum FuriganaMode
{
    AllKanji,
    UnknownAndLearningOnly,
    AuthorOnly,
    Hover,
    Off
}

public sealed record EnrichmentOptions(
    FuriganaMode FuriganaMode = FuriganaMode.AllKanji,
    bool HighlightVocabulary = false,
    string WritingMode = "auto",
    string Theme = "paper"
);

public readonly record struct SourceRubySpan(
    int StartCharOffset,
    int EndCharOffset,
    string Surface,
    string Reading
);

public readonly record struct AnalyzedToken(
    int StartCharOffset,
    int EndCharOffset,
    string Surface,
    string Lemma,
    string NormalizedForm,
    string Reading,
    string DictionaryReading,
    string PartOfSpeech,
    string ConjugationType,
    string ConjugationForm,
    RubySource ReadingSource,
    bool IsContentWord
);

public readonly record struct DeinflectionCandidate(
    string BaseForm,
    string? ExpectedReadingPrefix,
    IReadOnlyList<string> RuleChain,
    string PartOfSpeechConstraint
);

public readonly record struct RubyUnit(
    int RelativeStart,
    int RelativeEnd,
    string BaseText,
    string? Reading,
    RubySource Source
);

public interface ITextTokenizer : IDisposable
{
    IReadOnlyList<AnalyzedToken> TokenizeParagraph(
        ReadOnlySpan<char> paragraphText,
        IReadOnlyList<SourceRubySpan> authorRubies);
}

public interface IDeinflector
{
    IReadOnlyList<DeinflectionCandidate> Deinflect(ReadOnlySpan<char> surface);
}

public interface IRubyAligner
{
    IReadOnlyList<RubyUnit> AlignRuby(
        ReadOnlySpan<char> surface,
        ReadOnlySpan<char> reading,
        IReadOnlyList<SourceRubySpan> overlappingAuthorRubies);
}

public interface ILanguagePlugin : IDisposable
{
    string LanguageCode { get; }
    string DisplayName { get; }
    LanguageCapabilities Capabilities { get; }

    ITextTokenizer CreateTokenizer();
    IDeinflector? GetDeinflector();
    IRubyAligner? GetRubyAligner();

    string NormalizeReading(ReadOnlySpan<char> reading);
    bool NeedsPhoneticAnnotation(Rune character);
}

public sealed class LanguagePluginRegistry : IDisposable
{
    private readonly Dictionary<string, ILanguagePlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private ILanguagePlugin? _fallbackPlugin;

    public void Register(ILanguagePlugin plugin, bool isFallback = false)
    {
        _plugins[plugin.LanguageCode] = plugin;
        if (isFallback || plugin.LanguageCode == "*")
        {
            _fallbackPlugin = plugin;
        }
    }

    public ILanguagePlugin Resolve(string? languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            string primary = languageCode.Trim().Split('-', '_')[0].ToLowerInvariant();
            if (_plugins.TryGetValue(primary, out var match))
            {
                return match;
            }
        }

        return _fallbackPlugin
            ?? _plugins.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No ILanguagePlugin registered in LanguagePluginRegistry.");
    }

    public IReadOnlyList<ILanguagePlugin> ListPlugins() => _plugins.Values.ToList();

    public void Dispose()
    {
        foreach (var p in _plugins.Values)
        {
            p.Dispose();
        }
    }
}

