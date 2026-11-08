namespace ImmersiveReading.Core.Plugins;

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
    bool IsContentWord,
    string PartOfSpeechSub = ""
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

