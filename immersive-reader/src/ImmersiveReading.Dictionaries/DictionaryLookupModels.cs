using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Dictionaries;

public sealed record LookupQuery(
    string BookId,
    string ChapterHref,
    int TokenId,
    int CharOffsetInToken = 0,
    int MaxResults = 25
);

public sealed record PitchAccentInfo(
    string Reading,
    int Position,
    string Pattern // "Atamadaka", "Nakadaka", "Odaka", "Heiban"
);

public sealed record FrequencyBadge(
    string DictionaryTitle,
    string FrequencyText,
    int? Rank
);

public sealed record DictionarySenseBlock(
    string DictionaryTitle,
    IReadOnlyList<string> Glosses,
    IReadOnlyList<string> Tags
);

public sealed record TermEntryViewModel(
    string Expression,
    string Reading,
    string ReadingNormalized,
    WordStatus UserStatus,                  // New, Learning, Known, Ignored
    string? ReviewedContextGloss,           // From study.config.json or user custom gloss
    string ReadingMatchStatus,              // 'epub_ruby_verified', 'mecab_matched', 'deinflected'
    IReadOnlyList<string> InflectionChain,  // e.g. ["past (-ta)", "polite (-masu)"]
    bool IsInBookDeck,                      // True if in book's Jiten CSV deck
    int? BookStudySetNumber,                // e.g. Set 4 (from vocabulary-sets.json if built)
    IReadOnlyList<PitchAccentInfo> Pitches, // Downstep positions for SVG pitch graph
    IReadOnlyList<FrequencyBadge> Frequencies,
    IReadOnlyList<DictionarySenseBlock> Senses
);

public sealed record KanjiEntryViewModel(
    string Character,
    IReadOnlyList<string> PrimaryMeanings,
    string PrimarySource,
    IReadOnlyList<string> OnReadings,
    IReadOnlyList<string> KunReadings,
    int? StrokeCount,
    string? Grade,
    string? Radical,
    IReadOnlyList<string> KanjiVgStrokes
);

public sealed record LookupResult(
    string BookId,
    string ChapterHref,
    int HighlightStartTokenId,
    int HighlightEndTokenId,
    string MatchedSurface,
    string SentenceText,
    IReadOnlyList<RubyUnit> SentenceRuby,
    IReadOnlyList<TermEntryViewModel> Terms,
    IReadOnlyList<KanjiEntryViewModel> Kanji
);

