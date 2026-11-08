using System.Text;

namespace ImmersiveReading.Core.Plugins;

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

public interface ILanguagePluginResolver
{
    ILanguagePlugin Resolve(string? languageCode);
    IReadOnlyList<ILanguagePlugin> ListPlugins();
}

