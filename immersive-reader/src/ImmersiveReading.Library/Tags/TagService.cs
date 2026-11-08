using ImmersiveReading.Core.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Tags;

public sealed class TagService
{
    private readonly LibraryDatabase _db;
    private readonly TimeProvider _timeProvider;

    public TagService(LibraryDatabase db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<TagRecord> ListTags()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc,
                   COUNT(bt.book_id) AS book_count
            FROM tags t
            LEFT JOIN book_tags bt ON bt.tag_id = t.tag_id
            GROUP BY t.tag_id
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;

        var list = new List<TagRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TagRecord(
                TagId: r.GetString(0),
                Name: r.GetString(1),
                ColorHex: r.GetString(2),
                SortOrder: r.GetInt32(3),
                BookCount: r.GetInt32(5),
                CreatedAtUtc: r.GetString(4)));
        }
        return list;
    }

    public TagRecord SaveTag(string? tagId, string name, string colorHex = "#64748b", int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));
        }

        string cleanName = name.Trim();
        string id = string.IsNullOrWhiteSpace(tagId)
            ? $"tag-{Guid.NewGuid():N}"[..16]
            : tagId.Trim();
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tags (tag_id, name, color_hex, sort_order, created_at_utc)
            VALUES ($id, $name, $color, $order, $now)
            ON CONFLICT(name) DO UPDATE SET
                color_hex = excluded.color_hex,
                sort_order = excluded.sort_order
            RETURNING tag_id, name, color_hex, sort_order, created_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", cleanName);
        cmd.Parameters.AddWithValue("$color", colorHex);
        cmd.Parameters.AddWithValue("$order", sortOrder);
        cmd.Parameters.AddWithValue("$now", now);

        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            return new TagRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), 0, r.GetString(4));
        }
        return new TagRecord(id, cleanName, colorHex, sortOrder, 0, now);
    }

    public void DeleteTag(string tagId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE tag_id = $id;";
        cmd.Parameters.AddWithValue("$id", tagId);
        cmd.ExecuteNonQuery();
    }

    public static Dictionary<string, List<TagRecord>> LoadAllFolderTags(SqliteConnection conn)
    {
        var map = new Dictionary<string, List<TagRecord>>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ft.folder_id, t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc
            FROM folder_tags ft
            JOIN tags t ON t.tag_id = ft.tag_id
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string fid = r.GetString(0);
            if (!map.TryGetValue(fid, out var list))
            {
                list = [];
                map[fid] = list;
            }
            list.Add(new TagRecord(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), 0, r.GetString(4)));
        }
        return map;
    }

    public static Dictionary<string, List<TagRecord>> LoadTagsForBooks(SqliteConnection conn, IReadOnlyList<string> bookIds)
    {
        var map = new Dictionary<string, List<TagRecord>>(StringComparer.Ordinal);
        if (bookIds.Count == 0) return map;

        using var cmd = conn.CreateCommand();
        var pNames = new List<string>(bookIds.Count);
        for (int i = 0; i < bookIds.Count; i++)
        {
            string p = $"$b_{i}";
            pNames.Add(p);
            cmd.Parameters.AddWithValue(p, bookIds[i]);
        }

        cmd.CommandText = $"""
            SELECT bt.book_id, t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc
            FROM book_tags bt
            JOIN tags t ON t.tag_id = bt.tag_id
            WHERE bt.book_id IN ({string.Join(", ", pNames)})
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string bid = r.GetString(0);
            if (!map.TryGetValue(bid, out var list))
            {
                list = [];
                map[bid] = list;
            }
            list.Add(new TagRecord(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), 0, r.GetString(4)));
        }
        return map;
    }

    public void SetBookTagsInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string bookId,
        IReadOnlyList<string> tagIds)
    {
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM book_tags WHERE book_id = $b;";
            del.Parameters.AddWithValue("$b", bookId);
            del.ExecuteNonQuery();
        }

        foreach (string tid in tagIds)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO book_tags (book_id, tag_id, assigned_at_utc) VALUES ($b, $t, $now);";
            ins.Parameters.AddWithValue("$b", bookId);
            ins.Parameters.AddWithValue("$t", tid);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
    }

    public void SetFolderTagsInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string folderId,
        IReadOnlyList<string> tagIds)
    {
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM folder_tags WHERE folder_id = $f;";
            del.Parameters.AddWithValue("$f", folderId);
            del.ExecuteNonQuery();
        }

        foreach (string tid in tagIds)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO folder_tags (folder_id, tag_id, assigned_at_utc) VALUES ($f, $t, $now);";
            ins.Parameters.AddWithValue("$f", folderId);
            ins.Parameters.AddWithValue("$t", tid);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
    }
}

