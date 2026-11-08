using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.Books;
using ImmersiveReading.Library.CustomFields;
using ImmersiveReading.Library.Folders;
using ImmersiveReading.Library.Tags;

namespace ImmersiveReading.Library;

/// <summary>
/// Facade over the focused library services (books, folders, tags, custom fields).
/// Keeps the original public API stable while all logic lives in single-responsibility services.
/// </summary>
public sealed class LibraryManager : IBookCatalog
{
    private readonly TagService _tags;
    private readonly CustomFieldService _customFields;
    private readonly FolderService _folders;
    private readonly BookQueryService _queries;
    private readonly BookMetadataService _metadata;
    private readonly BookImportService _import;

    public LibraryManager(LibraryDatabase db, string? enrichmentExtractorOutputDir = null, TimeProvider? timeProvider = null)
    {
        _tags = new TagService(db, timeProvider);
        _customFields = new CustomFieldService(db, timeProvider);
        _folders = new FolderService(db, _customFields, timeProvider);
        _queries = new BookQueryService(db, _tags, _customFields);
        _metadata = new BookMetadataService(db, _queries, _tags, _customFields, timeProvider);
        _import = new BookImportService(db, _folders, _tags, _customFields, _queries, enrichmentExtractorOutputDir, timeProvider);
    }

    // ---- Import & scanning ----

    public Task<BookDetail> ImportBookAsync(
        string epubFilePath,
        string? folderId = null,
        string? seriesNameOverride = null,
        double? seriesIndexOverride = null,
        IReadOnlyList<string>? tagIds = null,
        IReadOnlyList<CustomFieldValueInput>? customFields = null,
        bool computeDeepChapterStats = false,
        CancellationToken ct = default)
        => _import.ImportBookAsync(epubFilePath, folderId, seriesNameOverride, seriesIndexOverride, tagIds, customFields, computeDeepChapterStats, ct);

    public Task<ScanBatchResult> ScanDirectoryAsync(
        string rootDirectory,
        string? targetFolderId = null,
        bool recursive = true,
        bool autoCreateFolderHierarchyFromDirectories = false,
        IProgress<ScanBatchProgress>? progress = null,
        CancellationToken ct = default)
        => _import.ScanDirectoryAsync(rootDirectory, targetFolderId, recursive, autoCreateFolderHierarchyFromDirectories, progress, ct);

    // ---- Book queries ----

    public PagedBookListResult ListBooks(BookListQuery query) => _queries.ListBooks(query);
    public BookSummary? GetBookSummary(string bookId) => _queries.GetBookSummary(bookId);
    public string? GetEpubFilePath(string bookId) => _queries.GetEpubFilePath(bookId);
    public BookDetail? GetBookDetail(string bookId) => _queries.GetBookDetail(bookId);

    // ---- Book metadata & lifecycle ----

    public BookDetail UpdateBookMetadata(BookMetadataUpdateRequest req) => _metadata.UpdateBookMetadata(req);
    public int BulkUpdateBooks(BulkBookUpdatePatch patch) => _metadata.BulkUpdateBooks(patch);
    public BookDetail ResetBookMetadataToOpf(string bookId) => _metadata.ResetBookMetadataToOpf(bookId);
    public void DeleteBook(string bookId) => _metadata.DeleteBook(bookId);
    public int BulkDeleteBooks(IReadOnlyList<string> bookIds) => _metadata.BulkDeleteBooks(bookIds);
    public void ReorderBooks(IReadOnlyList<string> orderedBookIds) => _metadata.ReorderBooks(orderedBookIds);

    // ---- Folders ----

    public IReadOnlyList<LibraryFolderNode> GetFolderTree() => _folders.GetFolderTree();

    public LibraryFolderNode SaveFolder(
        string? folderId,
        string? parentFolderId,
        string name,
        string folderKind = "folder",
        string description = "",
        string icon = "folder",
        string? colorHex = null,
        int sortOrder = 0,
        string defaultBookSort = "series_or_title",
        IReadOnlyList<string>? tagIds = null,
        IReadOnlyList<CustomFieldValueInput>? customFields = null)
        => _folders.SaveFolder(folderId, parentFolderId, name, folderKind, description, icon, colorHex, sortOrder, defaultBookSort, tagIds, customFields);

    public void DeleteFolder(string folderId, bool reassignBooksToParent = true) => _folders.DeleteFolder(folderId, reassignBooksToParent);
    public bool MoveFolder(string folderId, string? newParentFolderId, int? newSortOrder = null) => _folders.MoveFolder(folderId, newParentFolderId, newSortOrder);
    public IReadOnlyList<string> PruneEmptyFolders() => _folders.PruneEmptyFolders();

    // ---- Tags ----

    public IReadOnlyList<TagRecord> ListTags() => _tags.ListTags();
    public TagRecord SaveTag(string? tagId, string name, string colorHex = "#64748b", int sortOrder = 0) => _tags.SaveTag(tagId, name, colorHex, sortOrder);
    public void DeleteTag(string tagId) => _tags.DeleteTag(tagId);

    // ---- Custom fields ----

    public IReadOnlyList<CustomFieldDefinition> ListCustomFieldDefinitions() => _customFields.ListCustomFieldDefinitions();

    public CustomFieldDefinition SaveCustomFieldDefinition(
        string fieldKey,
        string displayName,
        string fieldType = "text",
        string targetScope = "any",
        bool inheritToChildren = true,
        IReadOnlyList<string>? options = null,
        bool showOnCard = true,
        int sortOrder = 100)
        => _customFields.SaveCustomFieldDefinition(fieldKey, displayName, fieldType, targetScope, inheritToChildren, options, showOnCard, sortOrder);

    public void SetCustomFieldValue(string entityType, string entityId, string fieldKey, string valueText, double? valueNumber = null)
        => _customFields.SetCustomFieldValue(entityType, entityId, fieldKey, valueText, valueNumber);
}
