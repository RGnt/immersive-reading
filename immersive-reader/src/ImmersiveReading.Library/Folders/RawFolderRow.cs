namespace ImmersiveReading.Library.Folders;

internal sealed record RawFolderRow(
    string FolderId,
    string? ParentFolderId,
    string Name,
    string FolderKind,
    string Description,
    string Icon,
    string? ColorHex,
    int SortOrder,
    string DefaultBookSort
);

