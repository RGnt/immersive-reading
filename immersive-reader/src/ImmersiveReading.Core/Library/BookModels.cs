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
    bool IsExcluded = false,
    IReadOnlyList<string>? MemberSpineHrefs = null
)
{
    /// <summary>
    /// Every spine file rendered as part of this chapter, in reading order, primary first.
    /// A chapter is one ToC entry plus the following spine files up to the next entry (spec 12).
    /// <see cref="MemberSpineHrefs"/> is null for rows stored before chapter model v2.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Members =>
        MemberSpineHrefs is { Count: > 0 } members ? members : [SpineHref];

    public bool OwnsSpineHref(string spineHref) =>
        Members.Any(m => string.Equals(m, spineHref, StringComparison.OrdinalIgnoreCase));
}

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

