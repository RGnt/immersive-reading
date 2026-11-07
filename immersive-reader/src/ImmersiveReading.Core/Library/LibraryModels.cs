using System.Text.Json.Serialization;

namespace ImmersiveReading.Core.Library;

public sealed record OpfOriginalMetadata(
    string Title = "",
    string SortTitle = "",
    string Authors = "",
    string AuthorSort = "",
    string Isbn = "",
    string Publisher = "",
    string Imprint = "",
    string PublishedDate = "",
    string Language = "ja",
    string Description = "",
    string OpfIdentifier = "",
    string SeriesName = "",
    double? SeriesIndex = null,
    string WritingMode = "auto"
);

public sealed record BookChapterRecord(
    string BookId,
    int ChapterIndex,
    string ChapterId,
    string Title,
    string SpineHref,
    string FragmentId = "",
    string? EndSpineHref = null,
    string? EndFragmentId = null,
    int CharCount = 0,
    int CumulativeChars = 0,
    bool IsExcluded = false
);

public sealed record TagRecord(
    string TagId,
    string Name,
    string ColorHex = "#64748b",
    int SortOrder = 0,
    int BookCount = 0,
    string CreatedAtUtc = ""
);

public sealed record CustomFieldDefinition(
    string FieldKey,
    string DisplayName,
    string FieldType,           // "url" | "text" | "number" | "rating" | "boolean" | "select" | "file_path"
    string TargetScope = "any", // "any" | "folder" | "book"
    bool InheritToChildren = true,
    IReadOnlyList<string>? Options = null,
    bool ShowOnCard = true,
    int SortOrder = 0,
    string CreatedAtUtc = ""
);

public sealed record CustomFieldValueInput(
    string FieldKey,
    string ValueText,
    double? ValueNumber = null
);

public sealed record EffectiveCustomFieldValue(
    string FieldKey,
    string DisplayName,
    string FieldType,
    string EffectiveValueText,
    double? EffectiveValueNumber,
    bool IsInherited,
    string SourceEntityType,    // "book" | "folder"
    string SourceEntityId,
    string SourceEntityName
);

public sealed record LibraryFolderNode(
    string FolderId,
    string? ParentFolderId,
    string Name,
    string FolderKind = "folder", // "collection" | "category" | "series" | "folder"
    string Description = "",
    string Icon = "folder",
    string? ColorHex = null,
    int SortOrder = 0,
    string DefaultBookSort = "series_or_title",
    int DirectBookCount = 0,
    int TotalSubtreeBookCount = 0,
    IReadOnlyList<TagRecord>? Tags = null,
    IReadOnlyList<EffectiveCustomFieldValue>? CustomFields = null,
    IReadOnlyList<LibraryFolderNode>? Children = null
);

public sealed record BookSummary(
    string BookId,
    string EpubSha256,
    string? FolderId,
    string? FolderPathBreadcrumb,
    string Title,
    string SortTitle,
    string SeriesName,
    double? SeriesIndex,
    int CustomSortOrder,
    string Authors,
    string AuthorSort,
    string Isbn,
    string Publisher,
    string Imprint,
    string PublishedDate,
    string Language,
    string Description,
    bool IsMetadataEdited,
    string FolderPath,
    string EpubFileName,
    long FileSizeBytes,
    string? CoverCachePath,
    int ChapterCount,
    int TotalCharCount,
    bool StatsComputed,
    string WritingMode,
    string AddedAtUtc,
    string UpdatedAtUtc,
    string? LastOpenedAtUtc,
    bool HasStudyPack,
    double OverallCompletion,
    IReadOnlyList<TagRecord> Tags,
    IReadOnlyDictionary<string, EffectiveCustomFieldValue> CustomFields
);

public sealed record BookDetail(
    BookSummary Summary,
    OpfOriginalMetadata OpfOriginal,
    IReadOnlyList<BookChapterRecord> Chapters,
    IReadOnlyList<EffectiveCustomFieldValue> EffectiveCustomFields,
    IReadOnlyDictionary<string, string> DirectBookCustomFields
);

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

public sealed record BookMetadataUpdateRequest(
    string BookId,
    string? Title = null,
    string? SortTitle = null,
    string? Authors = null,
    string? AuthorSort = null,
    string? Isbn = null,
    string? Publisher = null,
    string? Imprint = null,
    string? PublishedDate = null,
    string? SeriesName = null,
    double? SeriesIndex = null,
    bool ClearSeriesIndex = false,
    string? Description = null,
    string? Language = null,
    string? WritingMode = null,
    string? FolderId = null,
    bool ClearFolderId = false,
    int? CustomSortOrder = null,
    IReadOnlyList<string>? TagIds = null,
    IReadOnlyList<CustomFieldValueInput>? CustomFields = null
);

public sealed record BulkBookUpdatePatch(
    IReadOnlyList<string> BookIds,
    string? FolderId = null,
    bool ClearFolderId = false,
    string? SeriesName = null,
    bool AutoNumberVolumes = false,
    double AutoNumberStart = 1.0,
    string? Authors = null,
    string? AuthorSort = null,
    string? Publisher = null,
    string? Imprint = null,
    string? Language = null,
    IReadOnlyList<string>? AddTagIds = null,
    IReadOnlyList<string>? RemoveTagIds = null,
    IReadOnlyList<CustomFieldValueInput>? CustomFields = null
);

public sealed record ScanBatchProgress(
    int ScannedFiles,
    int TotalFiles,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    string CurrentFile
);

public sealed record ScanBatchResult(
    int TotalFilesDiscovered,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    double ElapsedSeconds,
    IReadOnlyList<string> Errors
);

public sealed record VocabularyRecord(
    string Language,
    string Lemma,
    string Reading,
    ImmersiveReading.Core.Plugins.WordStatus Status,
    string? CustomGloss,
    int TimesLookedUp,
    string? FirstSeenBookId,
    string UpdatedAtUtc
);


