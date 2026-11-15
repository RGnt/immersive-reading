namespace ImmersiveReading.Core.Library;

public sealed record CustomFieldFilter(
    string FieldKey,
    string Operator, // "eq" | "contains" | "lte" | "gte" | "exists"
    string? ValueText = null,
    double? ValueNumber = null
);

public sealed record BookListQuery(
    string? FolderId = null,
    bool IncludeDescendants = true,
    bool UncategorizedOnly = false,
    IReadOnlyList<string>? TagIds = null,
    string TagMatchMode = "AND", // "AND" | "OR"
    string? Search = null,
    string? SeriesName = null,
    string? Language = null,
    IReadOnlyList<CustomFieldFilter>? CustomFieldFilters = null,
    string Sort = "series_or_title", // "series_or_title" | "custom" | "title" | "author" | "added" | "last_read" | "isbn"
    int Offset = 0,
    int Limit = 100,
    /// <summary>True: only favourites (the book's or its series'). Null: no filter.</summary>
    bool? Favorite = null,
    /// <summary>reading | planned | completed (the book's own status, else its series'), or "any" for every book on a list. Null: no filter.</summary>
    string? ReadingStatus = null
);

/// <summary>How many books are on each list (the sidebar shows these).</summary>
public sealed record ReadingListCounts(int Favorites, int Reading, int Planned, int Completed);

public sealed record PagedBookListResult(
    IReadOnlyList<BookSummary> Items,
    int TotalCount,
    int Offset,
    int Limit,
    double QueryElapsedMs
);

