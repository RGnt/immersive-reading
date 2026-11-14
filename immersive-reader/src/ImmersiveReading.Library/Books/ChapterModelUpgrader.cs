using System.Collections.Concurrent;
using System.Diagnostics;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

/// <summary>
/// Lazily converts a book stored with the legacy per-spine-file chapter list (model v1) to the grouped
/// model (v2, spec 12) the first time it is read. The chapter list is rebuilt from the EPUB and saved
/// positions that reference chapters (reading progress, bookmarks, quiz sessions) are remapped, all in
/// one transaction. A failure leaves the book untouched at v1 so the reader keeps working.
/// </summary>
internal sealed class ChapterModelUpgrader(LibraryDatabase db)
{
    private readonly ConcurrentDictionary<string, object> _locks = new();

    /// <returns>True if the book was upgraded by this call.</returns>
    public bool EnsureCurrent(string bookId)
    {
        // Cheap check outside the lock: nearly every call is for an already-current book.
        var info = ReadBookInfo(bookId);
        if (info is null || info.Version >= ChapterModel.CurrentVersion) return false;

        lock (_locks.GetOrAdd(bookId, _ => new object()))
        {
            info = ReadBookInfo(bookId);
            if (info is null || info.Version >= ChapterModel.CurrentVersion) return false;

            try
            {
                return Upgrade(bookId, info);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SqliteException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Chapter model upgrade skipped for '{bookId}': {ex.Message}");
                return false;
            }
        }
    }

    private sealed record BookInfo(int Version, string EpubPath, string FileName, bool StatsComputed);

    private BookInfo? ReadBookInfo(string bookId)
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chapter_model_version, folder_path, epub_file_name, stats_computed FROM books WHERE book_id = $id;";
        cmd.Parameters.AddWithValue("$id", bookId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new BookInfo(r.GetInt32(0), Path.Combine(r.GetString(1), r.GetString(2)), r.GetString(2), r.GetInt32(3) != 0);
    }

    private bool Upgrade(string bookId, BookInfo info)
    {
        if (!File.Exists(info.EpubPath))
        {
            Trace.TraceWarning($"Chapter model upgrade skipped for '{bookId}': EPUB not found at '{info.EpubPath}'.");
            return false;
        }

        ExtractedEpubPackage pkg;
        using (var fs = new FileStream(info.EpubPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pkg = EpubMetadataExtractor.ExtractFromStream(fs, info.FileName, computeDeepChapterStats: info.StatsComputed);
        }

        var chapters = pkg.Chapters.Select(c => c with { BookId = bookId }).ToList();
        if (chapters.Count == 0) return false; // nothing sensible to upgrade to; keep the old list

        // spine href (any member) -> owning chapter
        var owner = new Dictionary<string, (int Index, string Primary, bool IsPrimary)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in chapters)
        {
            foreach (var member in ch.Members)
            {
                owner.TryAdd(member, (ch.ChapterIndex, ch.SpineHref, string.Equals(member, ch.SpineHref, StringComparison.OrdinalIgnoreCase)));
            }
        }

        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();

        var oldIndexToHref = ReadOldChapterHrefs(conn, tx, bookId);

        ChapterStore.ReplaceChapters(conn, tx, bookId, chapters);

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE books
                SET chapter_count = $count,
                    total_char_count = CASE WHEN stats_computed = 1 THEN $chars ELSE total_char_count END,
                    chapter_model_version = $version
                WHERE book_id = $id;
                """;
            upd.Parameters.AddWithValue("$count", chapters.Count);
            upd.Parameters.AddWithValue("$chars", pkg.TotalCharCount);
            upd.Parameters.AddWithValue("$version", ChapterModel.CurrentVersion);
            upd.Parameters.AddWithValue("$id", bookId);
            upd.ExecuteNonQuery();
        }

        RemapProgress(conn, tx, bookId, owner);
        RemapBookmarks(conn, tx, bookId, owner);
        RemapQuizSessions(conn, tx, bookId, oldIndexToHref, owner);

        tx.Commit();
        return true;
    }

    private static Dictionary<int, string> ReadOldChapterHrefs(SqliteConnection conn, SqliteTransaction tx, string bookId)
    {
        var map = new Dictionary<int, string>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT chapter_index, spine_href FROM book_chapters WHERE book_id = $id;";
        cmd.Parameters.AddWithValue("$id", bookId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt32(0)] = r.GetString(1);
        return map;
    }

    /// <summary>
    /// Position inside the primary file is preserved exactly (it is rendered first, so token ids and
    /// offsets are unchanged). Position inside a continuation member degrades to the chapter start.
    /// </summary>
    private static void RemapProgress(
        SqliteConnection conn, SqliteTransaction tx, string bookId,
        Dictionary<string, (int Index, string Primary, bool IsPrimary)> owner)
    {
        string? href;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT current_spine_href FROM reading_progress WHERE book_id = $id;";
            read.Parameters.AddWithValue("$id", bookId);
            href = read.ExecuteScalar() as string;
        }
        if (href is null || !owner.TryGetValue(href, out var target)) return;

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = target.IsPrimary
            ? "UPDATE reading_progress SET current_chapter_index = $idx WHERE book_id = $id;"
            : """
              UPDATE reading_progress
              SET current_chapter_index = $idx, current_spine_href = $primary,
                  locator_token_id = NULL, locator_char_offset = 0, scroll_or_page_ratio = 0
              WHERE book_id = $id;
              """;
        upd.Parameters.AddWithValue("$idx", target.Index);
        upd.Parameters.AddWithValue("$id", bookId);
        if (!target.IsPrimary) upd.Parameters.AddWithValue("$primary", target.Primary);
        upd.ExecuteNonQuery();
    }

    private static void RemapBookmarks(
        SqliteConnection conn, SqliteTransaction tx, string bookId,
        Dictionary<string, (int Index, string Primary, bool IsPrimary)> owner)
    {
        var rows = new List<(string Id, string Href)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT bookmark_id, spine_href FROM bookmarks WHERE book_id = $id;";
            read.Parameters.AddWithValue("$id", bookId);
            using var r = read.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, href) in rows)
        {
            if (!owner.TryGetValue(href, out var target)) continue;

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = target.IsPrimary
                ? "UPDATE bookmarks SET chapter_index = $idx WHERE bookmark_id = $bm;"
                : """
                  UPDATE bookmarks
                  SET chapter_index = $idx, spine_href = $primary,
                      start_token_id = NULL, end_token_id = NULL, start_char_offset = 0, end_char_offset = 0
                  WHERE bookmark_id = $bm;
                  """;
            upd.Parameters.AddWithValue("$idx", target.Index);
            upd.Parameters.AddWithValue("$bm", id);
            if (!target.IsPrimary) upd.Parameters.AddWithValue("$primary", target.Primary);
            upd.ExecuteNonQuery();
        }
    }

    private static void RemapQuizSessions(
        SqliteConnection conn, SqliteTransaction tx, string bookId,
        Dictionary<int, string> oldIndexToHref,
        Dictionary<string, (int Index, string Primary, bool IsPrimary)> owner)
    {
        var rows = new List<(string Id, int OldIndex)>();
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT session_id, chapter_index FROM comprehension_sessions WHERE book_id = $id;";
            read.Parameters.AddWithValue("$id", bookId);
            try
            {
                using var r = read.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1)));
            }
            catch (SqliteException)
            {
                return; // legacy table absent in this database
            }
        }

        foreach (var (id, oldIndex) in rows)
        {
            if (!oldIndexToHref.TryGetValue(oldIndex, out var oldHref) || !owner.TryGetValue(oldHref, out var target)) continue;

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE comprehension_sessions SET chapter_index = $idx WHERE session_id = $sid;";
            upd.Parameters.AddWithValue("$idx", target.Index);
            upd.Parameters.AddWithValue("$sid", id);
            upd.ExecuteNonQuery();
        }
    }
}
