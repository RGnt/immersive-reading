namespace ImmersiveReading.Plugins.DeckBuilder;

// Deck building is adapted from the way jiten.moe builds decks (https://github.com/Sirush/Jiten, Apache-2.0).
// See NOTICE-Jiten.md for what was adapted from where.

/// <summary>One word of a built deck. The first appearance in the book decides its position.</summary>
public sealed class DeckWord
{
    /// <summary>De-duplication key (dictionary sequence + expression when resolved, else lemma + reading).</summary>
    public required string Key { get; init; }

    /// <summary>Word as written (dictionary form), the CSV <c>Word</c> column.</summary>
    public required string Word { get; init; }

    /// <summary>Hiragana reading, the CSV <c>ReadingKana</c> column.</summary>
    public string ReadingKana { get; set; } = "";

    /// <summary>Bracket furigana such as <c>食[た]べる</c>, the CSV <c>ReadingFurigana</c> column.</summary>
    public string ReadingFurigana { get; set; } = "";

    public int Occurrences { get; set; }

    /// <summary>Position of the first occurrence (0-based), defines chronological order.</summary>
    public int FirstOrder { get; init; }

    /// <summary>Dictionary entry sequence number (JMdict id for JMdict-derived dictionaries), 0 when unresolved.</summary>
    public long Sequence { get; set; }

    public string Definitions { get; set; } = "";
    public string PitchPositions { get; set; } = "";

    /// <summary>Lowest frequency rank found in the imported frequency dictionaries, 0 when unknown.</summary>
    public int FrequencyRank { get; set; }

    public string ExampleSentence { get; set; } = "";

    /// <summary>Major part of speech from the tokenizer of the first occurrence (e.g. 助詞), empty when unknown.</summary>
    public string PartOfSpeech { get; init; } = "";

    /// <summary>One of <see cref="DeckKinds"/>. Particles and auxiliaries are not vocabulary and are printed separately.</summary>
    public string Kind { get; init; } = DeckKinds.Vocabulary;

    /// <summary>Usage note for function words (added by the LLM enrichment), empty otherwise.</summary>
    public string UsageNote { get; set; } = "";
    public bool IsContentWord { get; init; }
    public bool ResolvedInDictionary { get; set; }
}

/// <summary>The <c>Kind</c> column of deck.csv. Everything that is not <see cref="Vocabulary"/> is a function word.</summary>
public static class DeckKinds
{
    public const string Vocabulary = "vocabulary";
    public const string Particle = "particle";
    public const string Auxiliary = "auxiliary";

    /// <summary>Deterministic classification from the tokenizer's major part of speech (IPADIC naming).</summary>
    public static string FromPartOfSpeech(string? pos) => pos switch
    {
        "助詞" => Particle,
        "助動詞" => Auxiliary,
        _ => Vocabulary
    };

    public static bool IsFunctionWord(string kind) => kind is Particle or Auxiliary;
}

public sealed record DeckStats(
    int CharacterCount,
    int WordCount,
    int UniqueWordCount,
    int UniqueWordUsedOnceCount,
    int UniqueKanjiCount,
    int UniqueKanjiUsedOnceCount,
    int SentenceCount,
    double DialoguePercentage,
    int UnresolvedWordCount,
    int ParticleCount = 0,
    int AuxiliaryCount = 0);

public sealed record DeckBuildResult(DeckStats Stats, IReadOnlyList<DeckWord> Words);

public enum DeckOrder
{
    Chronological,
    DeckFrequency,
    GlobalFrequency
}

public enum DeckDownloadType
{
    Full,
    TopN,
    TargetCoverage,
    MinOccurrences
}

public enum DeckFormat
{
    Csv,
    Txt,
    TxtRepeated
}

public sealed record DeckExportOptions(
    DeckFormat Format = DeckFormat.Csv,
    DeckOrder Order = DeckOrder.Chronological,
    DeckDownloadType Selection = DeckDownloadType.Full,
    /// <summary>TopN: word count. TargetCoverage: percent of all word occurrences (1-100). MinOccurrences: minimum count.</summary>
    double SelectionValue = 0,
    bool ExcludeKana = false,
    bool ExcludeExampleSentences = false,
    /// <summary>Drop particles and auxiliaries entirely (they are otherwise kept and marked in the Kind column).</summary>
    bool ExcludeFunctionWords = false);

public sealed record DeckBuildOptions(bool ContentWordsOnly = false);
