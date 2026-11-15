namespace ImmersiveReading.Core.Knowledge;

/// <summary>
/// What a body of knowledge is about. <c>book</c>: a book and the series it belongs to (id = a book id); <c>subject</c>: a field of
/// study such as physics (id = a subject key); <c>user</c>: what the person using the app told us (id = <c>me</c>).
/// </summary>
public readonly record struct KnowledgeScope(string Kind, string Id)
{
    public const string BookKind = "book", SubjectKind = "subject", UserKind = "user";

    public static readonly KnowledgeScope User = new(UserKind, "me");
    public static KnowledgeScope Book(string bookId) => new(BookKind, bookId);
    public static KnowledgeScope Subject(string subject) => new(SubjectKind, subject);
}

/// <summary>How a source decides what the asker may see.</summary>
public enum KnowledgeGating
{
    /// <summary>Everything the source holds is visible (what the user told us, a subject graph without a curriculum).</summary>
    None,
    /// <summary>Only what a reader knows at a chapter: the query **must** carry a viewpoint, otherwise it is refused (never answered with everything).</summary>
    StoryPosition
}

/// <summary>
/// From where the asker looks. For a book scope: the chapter the reader is about to read (what came before is known, this chapter
/// and later are not). <see cref="Unrestricted"/> asks for everything and is refused by gated sources.
/// </summary>
public sealed record KnowledgeViewpoint(int? ChapterIndex = null)
{
    public static readonly KnowledgeViewpoint Unrestricted = new();
    public static KnowledgeViewpoint AtChapter(int chapterIndex) => new(Math.Max(0, chapterIndex));
    public bool IsRestricted => ChapterIndex is not null;
}

/// <param name="Text">Words to match. Null or empty lists the most relevant or most recent entries instead.</param>
/// <param name="Kinds">Entry kinds wanted (<c>character</c>, <c>event</c>, <c>item</c>, <c>concept</c>, <c>fact</c>); null = all.</param>
/// <param name="Audience">Who is asking (for example <c>persona:prs-123</c>), so a source can hide what was shared with someone else only. Null = the owner (the user's own screens).</param>
public sealed record KnowledgeQuery(
    KnowledgeScope Scope,
    string? Text = null,
    IReadOnlyList<string>? Kinds = null,
    KnowledgeViewpoint? Viewpoint = null,
    string? Audience = null,
    int Limit = 10);

/// <param name="Text">Prompt-ready plain text: a profile block, an event summary, a fact.</param>
/// <param name="Relevance">0 to 1 for text queries; 0 when the entry was listed rather than matched.</param>
public sealed record KnowledgeEntry(
    string SourceId,
    KnowledgeScope Scope,
    string Id,
    string Kind,
    string Title,
    string Text,
    double Relevance = 0,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <param name="Subject">Optional short label ("favourite food", "sister").</param>
/// <param name="Origin">Where it came from, for the owner's review ("chat:abc123", "settings").</param>
/// <param name="SharedWith">Audiences that may see the fact; null or empty = every audience.</param>
public sealed record FactInput(
    KnowledgeScope Scope,
    string Text,
    string? Subject = null,
    string? Origin = null,
    IReadOnlyList<string>? SharedWith = null);

public sealed record KnowledgeSourceInfo(string SourceId, string DisplayName, IReadOnlyList<string> ScopeKinds, KnowledgeGating Gating, bool Writable);

/// <summary>Thrown when a gated source is asked without a viewpoint, or a fact cannot be stored.</summary>
public sealed class KnowledgeException(string message) : InvalidOperationException(message);

/// <summary>A body of knowledge that can be asked. Implemented by the story graph, the user memory, later a subject graph.</summary>
public interface IKnowledgeSource
{
    string SourceId { get; }
    string DisplayName { get; }
    IReadOnlyList<string> ScopeKinds { get; }
    KnowledgeGating Gating { get; }

    Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct);
}

/// <summary>A source that stores facts given while the app is used (the user's own memory).</summary>
public interface IWritableKnowledgeSource : IKnowledgeSource
{
    Task<KnowledgeEntry> RememberAsync(FactInput fact, CancellationToken ct);
    Task<bool> ForgetAsync(string factId, CancellationToken ct);
}

/// <summary>
/// The one place other features ask. Sources register themselves; a consumer names a scope, optionally a viewpoint, and gets
/// entries back without knowing which plugin or store produced them.
/// </summary>
public interface IKnowledgeHub
{
    IReadOnlyList<KnowledgeSourceInfo> Sources { get; }

    /// <summary>Entries from every source that serves the query's scope kind, best first. Gated sources refuse an unrestricted query (<see cref="KnowledgeException"/>).</summary>
    Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct = default);

    /// <summary>Stores a fact in the writable source that serves the fact's scope kind.</summary>
    Task<KnowledgeEntry> RememberAsync(FactInput fact, CancellationToken ct = default);

    Task<bool> ForgetAsync(string sourceId, string factId, CancellationToken ct = default);

    IDisposable RegisterSource(IKnowledgeSource source);
}
