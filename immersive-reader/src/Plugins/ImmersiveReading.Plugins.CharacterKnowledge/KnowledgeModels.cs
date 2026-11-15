using System.Text.Json;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

public sealed class CharacterKnowledgeSettings
{
    public string EndpointUrl { get; set; } = "http://127.0.0.1:1919/v1";
    public string ModelId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Thinking { get; set; } = "off";
    public int ContextTokens { get; set; }
    public string OutputLanguage { get; set; } = "Japanese";
    public int MaxInputChars { get; set; } = 90_000;
    public int RequestTimeoutSeconds { get; set; } = 900;
    public int MinProminence { get; set; } = 1;
    public bool ExtractStory { get; set; } = true;
    public string BuilderDirectory { get; set; } = "";
    public string PythonPath { get; set; } = "";
}

/// <summary>One volume of a series as the knowledge graph numbers it (1-based, in reading order).</summary>
public sealed record VolumeInfo(int Volume, string BookId, string Title, double? SeriesIndex, int ChapterCount);

/// <param name="SeriesKey">Folder name of the knowledge base: stable for a series name (or a standalone book).</param>
/// <param name="OrderChanged">The volumes now sort differently than when the knowledge base was built: a rebuild is needed.</param>
public sealed record SeriesInfo(string SeriesKey, string Title, bool IsSeries, IReadOnlyList<VolumeInfo> Volumes, bool OrderChanged)
{
    public int VolumeOf(string bookId) => Volumes.FirstOrDefault(v => v.BookId == bookId)?.Volume ?? 1;
}

public sealed record ChapterState(int Volume, int Chapter, string Title, string Status, int OpsApplied, int OpsRejected, string Error, string Model, string BuiltAt, string TextSha);

/// <param name="EventCards">Distinct events the reader can see at the end of the book (stored story cards).</param>
/// <param name="ItemCards">Distinct items the reader can see at the end of the book.</param>
public sealed record KnowledgeCounts(int Entities, int Relations, int Events, int Snapshots, int EventCards = 0, int ItemCards = 0);

/// <summary>Build state of the story pass (events and item holdings) of one chapter.</summary>
public sealed record StoryChapterState(int Volume, int Chapter, string Status, string Error, string TextSha);

public sealed record KnowledgeStatus(IReadOnlyList<ChapterState> Chapters, KnowledgeCounts Counts, IReadOnlyList<StoryChapterState>? StoryRows = null)
{
    public IReadOnlyList<StoryChapterState> StoryChapters => StoryRows ?? [];
}

/// <param name="Events">Newest first; each is the stored card exactly as the builder wrote it (kind, title, summary, participants, location, parent).</param>
public sealed record StoryEventList(int Total, IReadOnlyList<JsonElement> Events);

public sealed record CharacterEntry(string CharacterId, string Name, int Prominence);

/// <param name="Profile">The stored structured profile (traits, relationships, timeline, speech samples) exactly as the builder wrote it.</param>
/// <param name="Block">The rendered text block for prompts.</param>
public sealed record CharacterProfile(string CharacterId, string Name, JsonElement Profile, string Block);
