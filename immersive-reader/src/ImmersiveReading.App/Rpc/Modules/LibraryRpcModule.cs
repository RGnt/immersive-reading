using System.Text.Json;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class LibraryRpcModule(
    LibraryManager library,
    IRpcEventSink? events = null,
    Func<string?, string?>? folderPicker = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("library.listBooks", (req, _) => Task.FromResult<object?>(ListBooks(req)));
            yield return ("library.getBookDetail", (req, _) => Task.FromResult<object?>(GetBookDetail(req)));
            yield return ("library.importBook", (req, ct) => ImportBookAsync(req, ct));
            yield return ("library.importBookBytes", (req, ct) => ImportBookBytesAsync(req, ct));
            yield return ("library.scanDirectory", (req, ct) => ScanDirectoryAsync(req, ct));
            yield return ("library.pickDirectory", (req, ct) => PickDirectoryAsync(req, ct));
            yield return ("library.updateBookMetadata", (req, _) => Task.FromResult<object?>(UpdateBookMetadata(req)));
            yield return ("library.bulkUpdateBooks", (req, _) => Task.FromResult<object?>(BulkUpdateBooks(req)));
            yield return ("library.resetBookMetadataToOpf", (req, _) => Task.FromResult<object?>(ResetBookMetadataToOpf(req)));
            yield return ("library.deleteBook", (req, _) => Task.FromResult<object?>(DeleteBook(req)));
            yield return ("library.bulkDeleteBooks", (req, _) => Task.FromResult<object?>(BulkDeleteBooks(req)));
            yield return ("library.clearLibrary", (req, _) => Task.FromResult<object?>(ClearLibrary(req)));
            yield return ("library.pruneEmptyFolders", (req, _) => Task.FromResult<object?>(PruneEmptyFolders()));
            yield return ("library.moveFolder", (req, _) => Task.FromResult<object?>(MoveFolder(req)));
            yield return ("library.reorderBooks", (req, _) => Task.FromResult<object?>(ReorderBooks(req)));
            yield return ("library.getFolderTree", (req, _) => Task.FromResult<object?>(library.GetFolderTree()));
            yield return ("library.saveFolder", (req, _) => Task.FromResult<object?>(SaveFolder(req)));
            yield return ("library.deleteFolder", (req, _) => Task.FromResult<object?>(DeleteFolder(req)));
            yield return ("library.listTags", (req, _) => Task.FromResult<object?>(library.ListTags()));
            yield return ("library.saveTag", (req, _) => Task.FromResult<object?>(SaveTag(req)));
            yield return ("library.deleteTag", (req, _) => Task.FromResult<object?>(DeleteTag(req)));
            yield return ("library.listCustomFieldDefs", (req, _) => Task.FromResult<object?>(library.ListCustomFieldDefinitions()));
            yield return ("library.saveCustomFieldDef", (req, _) => Task.FromResult<object?>(SaveCustomFieldDef(req)));
            yield return ("library.setCustomFieldValue", (req, _) => Task.FromResult<object?>(SetCustomFieldValue(req)));
        }
    }

    private object? ListBooks(RpcRequest req)
    {
        var query = req.Bind<BookListQuery>() ?? new BookListQuery();
        return library.ListBooks(query);
    }

    private object? GetBookDetail(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        var detail = library.GetBookDetail(bookId)
            ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        string? foundCsvName = null;
        if (!string.IsNullOrWhiteSpace(detail.Summary.FolderPath) && Directory.Exists(detail.Summary.FolderPath))
        {
            var csv = Directory.EnumerateFiles(detail.Summary.FolderPath, "*.csv").FirstOrDefault();
            if (csv is not null)
            {
                foundCsvName = Path.GetFileName(csv);
            }
        }

        return new
        {
            detail.Summary,
            detail.OpfOriginal,
            detail.Chapters,
            detail.EffectiveCustomFields,
            detail.DirectBookCustomFields,
            hasDeckCsv = foundCsvName is not null,
            deckCsvFileName = foundCsvName
        };
    }

    private async Task<object?> ImportBookAsync(RpcRequest req, CancellationToken ct)
    {
        string epubPath = req.RequiredString("epubPath");
        string? folderId = req.OptionalString("folderId");
        string? seriesName = req.OptionalString("seriesName");
        double? seriesIdx = req.OptionalDouble("seriesIndex");
        return await library.ImportBookAsync(epubPath, folderId, seriesName, seriesIdx, ct: ct).ConfigureAwait(false);
    }

    private async Task<object?> ImportBookBytesAsync(RpcRequest req, CancellationToken ct)
    {
        string fileName = req.RequiredString("fileName");
        string base64 = req.RequiredString("fileBytesBase64");
        string? folderId = req.OptionalString("folderId");
        string? seriesName = req.OptionalString("seriesName");
        double? seriesIdx = req.OptionalDouble("seriesIndex");

        byte[] bytes = Convert.FromBase64String(base64);
        string tempDir = Path.Combine(Path.GetTempPath(), "ir-imports");
        Directory.CreateDirectory(tempDir);
        string cleanFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(cleanFileName) || !cleanFileName.EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
        {
            cleanFileName = $"imported-{Guid.NewGuid():N}.epub";
        }
        string tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}-{cleanFileName}");
        await File.WriteAllBytesAsync(tempFilePath, bytes, ct).ConfigureAwait(false);

        return await library.ImportBookAsync(tempFilePath, folderId, seriesName, seriesIdx, ct: ct).ConfigureAwait(false);
    }

    private async Task<object?> ScanDirectoryAsync(RpcRequest req, CancellationToken ct)
    {
        string rootDir = req.RequiredString("rootDirectory");
        string? targetFolderId = req.OptionalString("targetFolderId");
        bool recursive = req.OptionalBool("recursive") ?? true;
        bool autoFolders = req.OptionalBool("autoCreateFolders") ?? false;

        var progress = new Progress<ScanBatchProgress>(prog =>
        {
            events?.Emit("library.scanProgress", prog);
        });

        return await library.ScanDirectoryAsync(
            rootDir,
            targetFolderId,
            recursive,
            autoFolders,
            progress,
            ct).ConfigureAwait(false);
    }

    private Task<object?> PickDirectoryAsync(RpcRequest req, CancellationToken ct)
    {
        string? defaultDir = req.OptionalString("defaultPath");
        string? picked = folderPicker?.Invoke(defaultDir);
        return Task.FromResult<object?>(new
        {
            path = picked ?? "",
            cancelled = string.IsNullOrWhiteSpace(picked)
        });
    }

    private object? UpdateBookMetadata(RpcRequest req)
    {
        var updateReq = req.Bind<BookMetadataUpdateRequest>()
            ?? throw new ArgumentException("Invalid updateBookMetadata payload.");
        return library.UpdateBookMetadata(updateReq);
    }

    private object? BulkUpdateBooks(RpcRequest req)
    {
        var patch = req.Bind<BulkBookUpdatePatch>()
            ?? throw new ArgumentException("Invalid bulkUpdateBooks payload.");
        int updatedCount = library.BulkUpdateBooks(patch);
        return new { updatedCount };
    }

    private object? ResetBookMetadataToOpf(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        return library.ResetBookMetadataToOpf(bookId);
    }

    private object? DeleteBook(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        library.DeleteBook(bookId);
        return new { ok = true };
    }

    private object? BulkDeleteBooks(RpcRequest req)
    {
        var bookIds = req.StringList("bookIds");
        int deleted = library.BulkDeleteBooks(bookIds);
        return new { ok = true, deletedCount = deleted };
    }

    private object? ClearLibrary(RpcRequest req)
    {
        bool removeFolders = req.OptionalBool("removeFolders") ?? true;
        bool resetVocabulary = req.OptionalBool("resetVocabulary") ?? false;
        var res = library.ClearLibrary(new ClearLibraryOptions(removeFolders, resetVocabulary));
        events?.Emit("library.cleared", res);
        return new { ok = true, result = res };
    }

    private object? PruneEmptyFolders()
    {
        var pruned = library.PruneEmptyFolders();
        return new { ok = true, prunedCount = pruned.Count, prunedFolderNames = pruned, tree = library.GetFolderTree() };
    }

    private object? MoveFolder(RpcRequest req)
    {
        string folderId = req.RequiredString("folderId");
        string? newParentId = req.OptionalString("newParentFolderId");
        int? newSortOrder = req.OptionalInt("newSortOrder");
        bool moved = library.MoveFolder(folderId, newParentId, newSortOrder);
        return new { ok = moved, tree = library.GetFolderTree() };
    }

    private object? ReorderBooks(RpcRequest req)
    {
        var bookIds = req.StringList("bookIdsInOrder");
        library.ReorderBooks(bookIds);
        return new { ok = true };
    }

    private object? SaveFolder(RpcRequest req)
    {
        string? folderId = req.OptionalString("folderId");
        string? parentFolderId = req.OptionalString("parentFolderId");
        string name = req.RequiredString("name");
        string folderKind = req.OptionalString("folderKind") ?? "folder";
        string description = req.OptionalString("description") ?? "";
        int sortOrder = req.OptionalInt("sortOrder") ?? 0;
        string defaultSort = req.OptionalString("defaultBookSort") ?? "series_or_title";

        List<CustomFieldValueInput>? customFields = null;
        if (req.Params.TryGetProperty("customFields", out var cfEl) && cfEl.ValueKind == JsonValueKind.Array)
        {
            customFields = JsonSerializer.Deserialize<List<CustomFieldValueInput>>(cfEl.GetRawText(), RpcJson.Options);
        }

        var saved = library.SaveFolder(
            folderId,
            parentFolderId,
            name,
            folderKind,
            description,
            sortOrder: sortOrder,
            defaultBookSort: defaultSort,
            customFields: customFields);
        return new { saved, tree = library.GetFolderTree() };
    }

    private object? DeleteFolder(RpcRequest req)
    {
        string folderId = req.RequiredString("folderId");
        library.DeleteFolder(folderId, reassignBooksToParent: true);
        return new { ok = true, tree = library.GetFolderTree() };
    }

    private object? SaveTag(RpcRequest req)
    {
        string? tagId = req.OptionalString("tagId");
        string name = req.RequiredString("name");
        string colorHex = req.OptionalString("colorHex") ?? "#3b82f6";
        int sortOrder = req.OptionalInt("sortOrder") ?? 0;
        var tag = library.SaveTag(tagId, name, colorHex, sortOrder);
        return new { tag, tags = library.ListTags() };
    }

    private object? DeleteTag(RpcRequest req)
    {
        string tagId = req.RequiredString("tagId");
        library.DeleteTag(tagId);
        return new { ok = true, tags = library.ListTags() };
    }

    private object? SaveCustomFieldDef(RpcRequest req)
    {
        string fieldKey = req.RequiredString("fieldKey");
        string displayName = req.RequiredString("displayName");
        string fieldType = req.OptionalString("fieldType") ?? "text";
        string targetScope = req.OptionalString("targetScope") ?? "any";
        bool inherit = req.OptionalBool("inheritToChildren") ?? true;
        bool showOnCard = req.OptionalBool("showOnCard") ?? true;
        int sortOrder = req.OptionalInt("sortOrder") ?? 100;

        var def = library.SaveCustomFieldDefinition(
            fieldKey,
            displayName,
            fieldType,
            targetScope,
            inherit,
            showOnCard: showOnCard,
            sortOrder: sortOrder);
        return new { def, customFields = library.ListCustomFieldDefinitions() };
    }

    private object? SetCustomFieldValue(RpcRequest req)
    {
        string entityType = req.RequiredString("entityType");
        string entityId = req.RequiredString("entityId");
        string fieldKey = req.RequiredString("fieldKey");
        string valueText = req.OptionalString("valueText") ?? "";
        double? valueNumber = req.OptionalDouble("valueNumber");
        library.SetCustomFieldValue(entityType, entityId, fieldKey, valueText, valueNumber);
        return new { ok = true };
    }
}

