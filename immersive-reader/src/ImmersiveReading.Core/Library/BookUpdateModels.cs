namespace ImmersiveReading.Core.Library;

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

public sealed record ClearLibraryOptions(
    bool RemoveFolders = true,
    bool ResetVocabulary = false
);

public sealed record ClearLibraryResult(
    int BooksDeleted,
    int FoldersDeleted,
    int CoversDeleted,
    bool VocabularyReset
);

