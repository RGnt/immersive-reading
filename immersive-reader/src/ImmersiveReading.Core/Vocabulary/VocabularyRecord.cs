using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Core.Vocabulary;

public sealed record VocabularyRecord(
    string Language,
    string Lemma,
    string Reading,
    WordStatus Status,
    string? CustomGloss,
    int TimesLookedUp,
    string? FirstSeenBookId,
    string UpdatedAtUtc
);

