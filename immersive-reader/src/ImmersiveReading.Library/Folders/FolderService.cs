using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.CustomFields;
using ImmersiveReading.Library.Tags;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Folders;

public sealed class FolderService
{
    private readonly LibraryDatabase _db;
    private readonly CustomFieldService _customFieldService;
    private readonly TimeProvider _timeProvider;

    public FolderService(
        LibraryDatabase db,
        CustomFieldService customFieldService,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _customFieldService = customFieldService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<LibraryFolderNode> GetFolderTree()
    {
        using var conn = _db.OpenConnection();
        var rawFolders = LoadAllFoldersRaw(conn);
        var directCounts = LoadDirectBookCountsPerFolder(conn);
        var folderTagsMap = TagService.LoadAllFolderTags(conn);
        var fieldDefs = CustomFieldService.LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = CustomFieldService.LoadAllCustomFieldValues(conn);

        // Build parent -> children map
        var childrenMap = new Dictionary<string, List<RawFolderRow>>(StringComparer.Ordinal);
        var roots = new List<RawFolderRow>();
        var folderLookup = rawFolders.ToDictionary(f => f.FolderId, StringComparer.Ordinal);

        foreach (var f in rawFolders)
        {
            if (string.IsNullOrEmpty(f.ParentFolderId) || !folderLookup.ContainsKey(f.ParentFolderId))
            {
                roots.Add(f);
            }
            else
            {
                if (!childrenMap.TryGetValue(f.ParentFolderId, out var list))
                {
                    list = [];
                    childrenMap[f.ParentFolderId] = list;
                }
                list.Add(f);
            }
        }

        LibraryFolderNode BuildNode(RawFolderRow row)
        {
            var childNodes = new List<LibraryFolderNode>();
            if (childrenMap.TryGetValue(row.FolderId, out var childRows))
            {
                foreach (var cr in childRows.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    childNodes.Add(BuildNode(cr));
                }
            }

            int direct = directCounts.GetValueOrDefault(row.FolderId, 0);
            int subtree = direct + childNodes.Sum(c => c.TotalSubtreeBookCount);
            var tags = folderTagsMap.GetValueOrDefault(row.FolderId) ?? [];
            var effFields = CustomFieldService.ResolveEffectiveCustomFieldsForFolder(row.FolderId, folderLookup, fieldDefs, allCustomValues);

            return new LibraryFolderNode(
                FolderId: row.FolderId,
                ParentFolderId: row.ParentFolderId,
                Name: row.Name,
                FolderKind: row.FolderKind,
                Description: row.Description,
                Icon: row.Icon,
                ColorHex: row.ColorHex,
                SortOrder: row.SortOrder,
                DefaultBookSort: row.DefaultBookSort,
                DirectBookCount: direct,
                TotalSubtreeBookCount: subtree,
                Tags: tags,
                CustomFields: effFields,
                Children: childNodes);
        }

        return roots
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildNode)
            .ToList();
    }

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
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Folder name cannot be empty.", nameof(name));
        }

        string id = string.IsNullOrWhiteSpace(folderId)
            ? $"fld-{Guid.NewGuid():N}"[..20]
            : folderId.Trim();

        if (string.Equals(id, parentFolderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A folder cannot be its own parent.");
        }

        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO library_folders (
                    folder_id, parent_folder_id, name, folder_kind, description, icon, color_hex,
                    sort_order, default_book_sort, created_at_utc, updated_at_utc
                ) VALUES (
                    $id, $parent, $name, $kind, $desc, $icon, $color,
                    $order, $sort, $now, $now
                )
                ON CONFLICT(folder_id) DO UPDATE SET
                    parent_folder_id = excluded.parent_folder_id,
                    name = excluded.name,
                    folder_kind = excluded.folder_kind,
                    description = excluded.description,
                    icon = excluded.icon,
                    color_hex = excluded.color_hex,
                    sort_order = excluded.sort_order,
                    default_book_sort = excluded.default_book_sort,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$parent", (object?)parentFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$kind", folderKind);
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$icon", icon);
            cmd.Parameters.AddWithValue("$color", (object?)colorHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", sortOrder);
            cmd.Parameters.AddWithValue("$sort", defaultBookSort);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        if (tagIds is not null)
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM folder_tags WHERE folder_id = $f;";
                del.Parameters.AddWithValue("$f", id);
                del.ExecuteNonQuery();
            }
            foreach (string tid in tagIds)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO folder_tags (folder_id, tag_id, assigned_at_utc) VALUES ($f, $t, $now);";
                ins.Parameters.AddWithValue("$f", id);
                ins.Parameters.AddWithValue("$t", tid);
                ins.Parameters.AddWithValue("$now", now);
                ins.ExecuteNonQuery();
            }
        }

        if (customFields is not null)
        {
            _customFieldService.SetEntityCustomFieldsInTransaction(conn, tx, "folder", id, customFields);
        }

        tx.Commit();

        return FindFolderNodeInTree(GetFolderTree(), id)
            ?? new LibraryFolderNode(id, parentFolderId, name, folderKind, description, icon, colorHex, sortOrder, defaultBookSort);
    }

    public void DeleteFolder(string folderId, bool reassignBooksToParent = true)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        if (reassignBooksToParent)
        {
            string? parentId = null;
            using (var getP = conn.CreateCommand())
            {
                getP.Transaction = tx;
                getP.CommandText = "SELECT parent_folder_id FROM library_folders WHERE folder_id = $id;";
                getP.Parameters.AddWithValue("$id", folderId);
                parentId = getP.ExecuteScalar() as string;
            }

            using (var updB = conn.CreateCommand())
            {
                updB.Transaction = tx;
                updB.CommandText = "UPDATE books SET folder_id = $parent WHERE folder_id = $id;";
                updB.Parameters.AddWithValue("$id", folderId);
                updB.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
                updB.ExecuteNonQuery();
            }

            using (var updF = conn.CreateCommand())
            {
                updF.Transaction = tx;
                updF.CommandText = "UPDATE library_folders SET parent_folder_id = $parent WHERE parent_folder_id = $id;";
                updF.Parameters.AddWithValue("$id", folderId);
                updF.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
                updF.ExecuteNonQuery();
            }
        }

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM library_folders WHERE folder_id = $id;";
            del.Parameters.AddWithValue("$id", folderId);
            del.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public bool MoveFolder(string folderId, string? newParentFolderId, int? newSortOrder = null)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return false;
        if (folderId == newParentFolderId) return false;

        using var conn = _db.OpenConnection();
        if (!string.IsNullOrWhiteSpace(newParentFolderId))
        {
            string? curr = newParentFolderId;
            while (!string.IsNullOrWhiteSpace(curr))
            {
                if (curr == folderId) return false; // Cycle detected
                using var pCmd = conn.CreateCommand();
                pCmd.CommandText = "SELECT parent_folder_id FROM library_folders WHERE folder_id = $id;";
                pCmd.Parameters.AddWithValue("$id", curr);
                curr = pCmd.ExecuteScalar() as string;
            }
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE library_folders
                SET parent_folder_id = $parent,
                    sort_order = COALESCE($order, sort_order)
                WHERE folder_id = $id;
                """;
            cmd.Parameters.AddWithValue("$parent", (object?)newParentFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", (object?)newSortOrder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> PruneEmptyFolders()
    {
        using var conn = _db.OpenConnection();
        var prunedNames = new List<string>();

        while (true)
        {
            var leafEmpty = new List<(string Id, string Name)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT f.folder_id, f.name
                    FROM library_folders f
                    WHERE (SELECT COUNT(*) FROM books b WHERE b.folder_id = f.folder_id) = 0
                      AND (SELECT COUNT(*) FROM library_folders c WHERE c.parent_folder_id = f.folder_id) = 0;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    leafEmpty.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            if (leafEmpty.Count == 0) break;

            using var tx = conn.BeginTransaction();
            foreach (var (fId, name) in leafEmpty)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM library_folders WHERE folder_id = $id;";
                delCmd.Parameters.AddWithValue("$id", fId);
                delCmd.ExecuteNonQuery();
                prunedNames.Add(name);
            }
            tx.Commit();
        }

        return prunedNames;
    }

    public static LibraryFolderNode? FindFolderNodeInTree(IEnumerable<LibraryFolderNode> nodes, string targetId)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.FolderId, targetId, StringComparison.Ordinal)) return n;
            if (n.Children is { Count: > 0 })
            {
                var found = FindFolderNodeInTree(n.Children, targetId);
                if (found is not null) return found;
            }
        }
        return null;
    }

    internal static string? BuildFolderBreadcrumb(string? folderId, Dictionary<string, RawFolderRow> folderLookup)
    {
        if (string.IsNullOrEmpty(folderId) || !folderLookup.ContainsKey(folderId)) return null;
        var parts = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cur = folderId;
        while (!string.IsNullOrEmpty(cur) && visited.Add(cur) && folderLookup.TryGetValue(cur, out var row))
        {
            parts.Add(row.Name);
            cur = row.ParentFolderId;
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    internal static HashSet<string> GetSubtreeFolderIds(string rootFolderId, List<RawFolderRow> allFolders)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootFolderId };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var f in allFolders)
            {
                if (f.ParentFolderId is not null && result.Contains(f.ParentFolderId) && result.Add(f.FolderId))
                {
                    added = true;
                }
            }
        }
        return result;
    }

    internal static List<RawFolderRow> LoadAllFoldersRaw(SqliteConnection conn)
    {
        var list = new List<RawFolderRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT folder_id, parent_folder_id, name, folder_kind, description, icon, color_hex, sort_order, default_book_sort
            FROM library_folders
            ORDER BY sort_order ASC, name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RawFolderRow(
                FolderId: r.GetString(0),
                ParentFolderId: r.IsDBNull(1) ? null : r.GetString(1),
                Name: r.GetString(2),
                FolderKind: r.GetString(3),
                Description: r.GetString(4),
                Icon: r.GetString(5),
                ColorHex: r.IsDBNull(6) ? null : r.GetString(6),
                SortOrder: r.GetInt32(7),
                DefaultBookSort: r.GetString(8)));
        }
        return list;
    }

    public static Dictionary<string, int> LoadDirectBookCountsPerFolder(SqliteConnection conn)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT folder_id, COUNT(*) FROM books WHERE folder_id IS NOT NULL GROUP BY folder_id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = r.GetInt32(1);
        }
        return map;
    }

    public string? ResolveOrCreateFolderForDiskDirectory(
        SqliteConnection conn,
        SqliteTransaction tx,
        string rootDir,
        string currentDir,
        Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(currentDir, out string? cachedId))
        {
            return cachedId;
        }

        string rel = Path.GetRelativePath(rootDir, currentDir);
        if (rel == "." || string.IsNullOrWhiteSpace(rel) || rel.StartsWith("..", StringComparison.Ordinal))
        {
            return cache.GetValueOrDefault(rootDir);
        }

        int epubCountInDir = Directory.EnumerateFiles(currentDir, "*.epub", SearchOption.TopDirectoryOnly).Take(2).Count();
        int subDirCount = Directory.EnumerateDirectories(currentDir).Take(1).Count();
        string? parentDir = Path.GetDirectoryName(currentDir);

        if (epubCountInDir == 1 && subDirCount == 0 && parentDir is not null)
        {
            string? parentFolderId = ResolveOrCreateFolderForDiskDirectory(conn, tx, rootDir, parentDir, cache);
            cache[currentDir] = parentFolderId;
            return parentFolderId;
        }

        string? parentId = parentDir is not null
            ? ResolveOrCreateFolderForDiskDirectory(conn, tx, rootDir, parentDir, cache)
            : cache.GetValueOrDefault(rootDir);

        string dirName = Path.GetFileName(currentDir);
        string folderId = EnsureChildFolderInTransaction(conn, tx, parentId, dirName);
        cache[currentDir] = folderId;
        return folderId;
    }

    public string EnsureChildFolderInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string? parentFolderId,
        string name)
    {
        using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = parentFolderId is null
                ? "SELECT folder_id FROM library_folders WHERE parent_folder_id IS NULL AND name = $name LIMIT 1;"
                : "SELECT folder_id FROM library_folders WHERE parent_folder_id = $parent AND name = $name LIMIT 1;";
            findCmd.Parameters.AddWithValue("$name", name);
            if (parentFolderId is not null)
            {
                findCmd.Parameters.AddWithValue("$parent", parentFolderId);
            }
            if (findCmd.ExecuteScalar() is string existingId)
            {
                return existingId;
            }
        }

        string newId = $"fld-{Guid.NewGuid():N}"[..20];
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        using var insCmd = conn.CreateCommand();
        insCmd.Transaction = tx;
        insCmd.CommandText = """
            INSERT INTO library_folders (
                folder_id, parent_folder_id, name, folder_kind, description, icon, sort_order, default_book_sort, created_at_utc, updated_at_utc
            ) VALUES (
                $id, $parent, $name, 'folder', '', 'folder', 0, 'series_or_title', $now, $now
            );
            """;
        insCmd.Parameters.AddWithValue("$id", newId);
        insCmd.Parameters.AddWithValue("$parent", (object?)parentFolderId ?? DBNull.Value);
        insCmd.Parameters.AddWithValue("$name", name);
        insCmd.Parameters.AddWithValue("$now", now);
        insCmd.ExecuteNonQuery();
        return newId;
    }
}
