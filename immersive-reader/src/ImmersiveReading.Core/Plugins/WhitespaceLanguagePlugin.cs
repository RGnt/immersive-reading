using System.Text;

namespace ImmersiveReading.Core.Plugins;

/// <summary>
/// Built-in language plugin for space-delimited languages (Spanish, English, German, French, etc.).
/// Tokenizes word runs while keeping spaces and punctuation outside word token spans.
/// </summary>
public sealed class WhitespaceLanguagePlugin : ILanguagePlugin, ITextTokenizer
{
    public string LanguageCode => "*";
    public string DisplayName => "Space-Delimited Language Tokenizer (Default)";
    public LanguageCapabilities Capabilities => LanguageCapabilities.None;

    public ITextTokenizer CreateTokenizer() => this;
    public IDeinflector? GetDeinflector() => null;
    public IRubyAligner? GetRubyAligner() => null;

    public string NormalizeReading(ReadOnlySpan<char> reading) => reading.ToString().ToLowerInvariant();
    public bool NeedsPhoneticAnnotation(Rune character) => false;

    public IReadOnlyList<AnalyzedToken> TokenizeParagraph(
        ReadOnlySpan<char> paragraphText,
        IReadOnlyList<SourceRubySpan> authorRubies)
    {
        var tokens = new List<AnalyzedToken>();
        int i = 0;
        while (i < paragraphText.Length)
        {
            if (!IsWordChar(paragraphText[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < paragraphText.Length && IsWordChar(paragraphText[i]))
            {
                i++;
            }

            string surface = paragraphText[start..i].ToString();
            string lemma = surface.ToLowerInvariant();
            tokens.Add(new AnalyzedToken(
                StartCharOffset: start,
                EndCharOffset: i,
                Surface: surface,
                Lemma: lemma,
                NormalizedForm: lemma,
                Reading: lemma,
                DictionaryReading: lemma,
                PartOfSpeech: "Word",
                ConjugationType: "",
                ConjugationForm: "",
                ReadingSource: RubySource.None,
                IsContentWord: true));
        }

        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-';

    public void Dispose()
    {
    }
}

