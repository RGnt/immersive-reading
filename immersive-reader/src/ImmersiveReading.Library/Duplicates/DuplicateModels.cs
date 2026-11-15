namespace ImmersiveReading.Library.Duplicates;

/// <summary>What the detector needs to know about one library book (spec 21). Built from the books table in one query.</summary>
public sealed record BookFacts(
    string BookId,
    string Title,
    string SeriesName,
    double? SeriesIndex,
    string Authors,
    string Isbn,
    string Identifier,
    string Language,
    string Publisher,
    string Description,
    int TotalChars,
    int ChapterCount,
    bool StatsComputed,
    long FileSize,
    string EpubFileName,
    string FolderPath,
    string AddedAtUtc,
    bool HasCover,
    double ProgressPercent,
    bool HasStudyPack,
    bool HasDeckCsv,
    int TagCount);

public sealed class DuplicateOptions
{
    /// <summary>Pairs below this score are not proposed (spec 21 section 3.2). 50 = "possible".</summary>
    public int MinScore { get; init; } = 50;
    /// <summary>Fuzzy-title buckets larger than this are skipped (one author with thousands of same-volume books).</summary>
    public int MaxFuzzyBucket { get; init; } = 200;
    /// <summary>An identifier shared by more than this many books is not a publication id (some tools stamp every book alike).</summary>
    public int MaxIdentifierBucket { get; init; } = 5;
}

public sealed record DuplicatePair(string BookA, string BookB, int Score, IReadOnlyList<string> Reasons);

public sealed record DuplicateMember(BookFacts Book, int KeepScore, IReadOnlyList<string> KeepReasons, int BestMatchScore);

/// <param name="Confidence">"certain" (90+), "likely" (70-89) or "possible" (50-69), from the strongest pair.</param>
public sealed record DuplicateGroup(
    string GroupId,
    string Confidence,
    int Score,
    string RecommendedKeepId,
    IReadOnlyList<DuplicateMember> Members,
    IReadOnlyList<DuplicatePair> Pairs);

public sealed record DuplicateSearchResult(int TotalBooks, long ElapsedMs, IReadOnlyList<DuplicateGroup> Groups);

public sealed record ResolveResult(int Removed, int TagsMerged, int CustomFieldsMerged, int FieldsFilled);

/// <summary>One group of a mass clear: keep one book, remove the others.</summary>
public sealed record ResolveGroupRequest(string KeepBookId, IReadOnlyList<string> RemoveBookIds);

public sealed record ResolveGroupFailure(string KeepBookId, string Error);

/// <summary>Result of a mass clear. Groups that no longer fit (a book vanished since the search) are reported, never abort the rest.</summary>
public sealed record BatchResolveResult(int GroupsResolved, int Removed, int TagsMerged, int CustomFieldsMerged, int FieldsFilled, IReadOnlyList<string> ResolvedKeepBookIds, IReadOnlyList<ResolveGroupFailure> Failed);

public sealed record RemovedBook(string BookId, string Title, string Authors, string EpubFileName, string OriginalFolderPath, string KeptBookId, string RemovedAtUtc);
