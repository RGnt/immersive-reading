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

