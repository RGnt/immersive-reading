namespace ImmersiveReading.Plugins.DeckBuilder;

/// <summary>A word to look up: <see cref="Key"/> identifies the request in the result map.</summary>
public sealed record DictQuery(string Key, string Lemma, string Reading);

public sealed record DictEntry(
    string Expression,
    string Reading,
    long Sequence,
    string Definitions,
    string PitchPositions,
    int FrequencyRank);

/// <summary>Resolves lemmas to dictionary entries. Implementations must be offline and deterministic.</summary>
public interface IDeckDictionary
{
    IReadOnlyDictionary<string, DictEntry> Resolve(IReadOnlyCollection<DictQuery> queries);
}
