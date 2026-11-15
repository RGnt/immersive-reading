namespace ImmersiveReading.Plugins.DeckBuilder.Enrichment;

/// <summary>The sentences of one chapter, the unit that is sent to the LLM.</summary>
public sealed record ChapterSentences(int ChapterIndex, string Title, IReadOnlyList<string> Sentences);

/// <summary>A sentence of the book that illustrates a grammar point. <see cref="Surface"/> is verbatim inside <see cref="Text"/>.</summary>
public sealed record GrammarExample(string Text, string Surface, int ChapterIndex);

public sealed class GrammarPoint
{
    public required string Id { get; init; }
    public required string Pattern { get; init; }
    /// <summary>grammar, expression or conjugation.</summary>
    public required string Category { get; init; }
    public string Meaning { get; set; } = "";
    public string Explanation { get; set; } = "";
    public int FirstChapterIndex { get; init; }
    public string FirstChapterTitle { get; init; } = "";
    public List<GrammarExample> Examples { get; set; } = [];
}

/// <summary>One way a particle / auxiliary is used in the book.</summary>
public sealed record FunctionUse(string Function, string Note, string? Example);

public sealed class EnrichmentResult
{
    public string Model { get; set; } = "";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Identifies the book file the result was made for (EPUB write time); a changed book invalidates it.</summary>
    public string BookStamp { get; set; } = "";
    public int ChunksTotal { get; set; }
    public int ChunksFailed { get; set; }
    public List<GrammarPoint> Points { get; set; } = [];
    /// <summary>Particle / auxiliary (deck word) → its distinct uses.</summary>
    public Dictionary<string, List<FunctionUse>> FunctionUses { get; set; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// Progress of a run. The counters are **live**: what has been accepted so far, so a running job can show real numbers instead of
/// zeros until the end (the result is only complete, and saved, when every chunk is done).
/// </summary>
public sealed record EnrichmentProgress(
    int Done, int Total, string Chapter,
    int GrammarPoints = 0, int FunctionWords = 0, int ChunksFailed = 0, int WarningCount = 0,
    IReadOnlyList<string>? RecentWarnings = null);

public sealed record DeckEnrichmentOptions(
    /// <summary>Largest amount of chapter text (characters) sent in one request. 256k-token models take much more; 60k is a safe default.</summary>
    int MaxChunkChars = 60_000,
    int MaxPointsPerChunk = 25);
