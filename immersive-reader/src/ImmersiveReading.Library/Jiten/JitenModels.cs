namespace ImmersiveReading.Library;

public record JitenDeckTag(string Name, int Percentage);

public record JitenDeckLink(int LinkType, string TypeName, string Url);

public record JitenDeckSummary(
    long DeckId,
    string OriginalTitle,
    string? RomajiTitle,
    string? EnglishTitle,
    string? CoverUrl,
    int MediaType,
    string MediaTypeName,
    string? Description,
    int CharacterCount,
    int WordCount,
    int UniqueWordCount,
    int UniqueKanjiCount,
    double Difficulty,
    double DifficultyRaw,
    int ChildrenDeckCount,
    IReadOnlyList<string> Genres,
    IReadOnlyList<JitenDeckTag> Tags,
    IReadOnlyList<JitenDeckLink> Links,
    string JitenUrl
);

public record JitenDeckDetailResult(
    JitenDeckSummary? MainDeck,
    IReadOnlyList<JitenDeckSummary> SubDecks
);

public record JitenProgressionSegment(
    int Segment,
    double Difficulty,
    double Peak
);

public record JitenDifficultyResult(
    long DeckId,
    double Difficulty,
    double Peak,
    int DistinctVoterCount,
    IReadOnlyDictionary<string, double> Deciles,
    IReadOnlyList<JitenProgressionSegment> Progression
);

public record JitenDeckStatsResult(
    long DeckId,
    int TotalUniqueWords,
    double RSquared,
    string? ComputedAt,
    IReadOnlyDictionary<string, int> Milestones
);

public record JitenVocabularyPreviewWord(
    long WordId,
    int ReadingIndex,
    string Reading,
    string ReadingFurigana,
    string MainDefinition,
    int FrequencyRank,
    int Occurrences
);

