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
    int Limit = 100
);

public sealed record PagedBookListResult(
    IReadOnlyList<BookSummary> Items,
    int TotalCount,
    int Offset,
    int Limit,
    double QueryElapsedMs
);

