namespace ImmersiveReading.Core.Library;

public sealed record TagRecord(
    string TagId,
    string Name,
    string ColorHex = "#64748b",
    int SortOrder = 0,
    int BookCount = 0,
    string CreatedAtUtc = ""
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

