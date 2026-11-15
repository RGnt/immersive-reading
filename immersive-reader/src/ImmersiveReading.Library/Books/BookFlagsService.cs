using ImmersiveReading.Core.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

/// <summary>
/// Favourites and the reading list ("currently reading", "planning to read", "completed"), kept per book and per series. A book's own flag wins;
/// a book without its own reading status inherits its series' status, and a book is a favourite when it or its series is. The effective values
/// are what the list filters and the sidebar counts use; the raw flags (own and series) travel in <see cref="BookSummary"/> so a screen can edit
/// each one.
/// </summary>
public sealed class BookFlagsService(LibraryDatabase db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>SQL: the book (alias b) is a favourite itself or through its series.</summary>
    public const string EffectiveFavoriteSql =
        "(b.is_favorite = 1 OR (b.series_name <> '' AND EXISTS (SELECT 1 FROM series_flags sf WHERE sf.series_name = b.series_name AND sf.is_favorite = 1)))";

    /// <summary>SQL: the reading status of the book (alias b): its own, else its series', else ''.</summary>
    public const string EffectiveStatusSql =
        "(CASE WHEN b.reading_status <> '' THEN b.reading_status ELSE COALESCE((SELECT sf.reading_status FROM series_flags sf WHERE sf.series_name = b.series_name AND b.series_name <> ''), '') END)";

    /// <param name="favorite">Null leaves it as it is.</param>
    /// <param name="readingStatus">Null leaves it as it is; "" clears it; otherwise reading, planned or completed.</param>
    public int SetBookFlags(IReadOnlyList<string> bookIds, bool? favorite, string? readingStatus)
    {
        string? status = ReadingStatus.Normalize(readingStatus);
        if (bookIds is null || bookIds.Count == 0 || (favorite is null && status is null)) return 0;
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string now = _time.GetUtcNow().UtcDateTime.ToString("O");
        int updated = 0;
        foreach (string id in bookIds.Distinct(StringComparer.Ordinal))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE books SET
                    is_favorite = COALESCE($fav, is_favorite),
                    reading_status = COALESCE($status, reading_status),
                    updated_at_utc = $now
                WHERE book_id = $id;
                """;
            cmd.Parameters.AddWithValue("$fav", favorite is null ? DBNull.Value : favorite.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", id);
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return updated;
    }

    public void SetSeriesFlags(string seriesName, bool? favorite, string? readingStatus)
    {
        string name = (seriesName ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("A series name is required.");
        string? status = ReadingStatus.Normalize(readingStatus);
        if (favorite is null && status is null) return;
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO series_flags (series_name, is_favorite, reading_status, updated_at_utc)
            VALUES ($name, COALESCE($fav, 0), COALESCE($status, ''), $now)
            ON CONFLICT(series_name) DO UPDATE SET
                is_favorite = COALESCE($fav, series_flags.is_favorite),
                reading_status = COALESCE($status, series_flags.reading_status),
                updated_at_utc = $now;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$fav", favorite is null ? DBNull.Value : favorite.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcDateTime.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public ReadingListCounts GetCounts()
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                COALESCE(SUM(CASE WHEN {EffectiveFavoriteSql} THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN {EffectiveStatusSql} = 'reading' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN {EffectiveStatusSql} = 'planned' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN {EffectiveStatusSql} = 'completed' THEN 1 ELSE 0 END), 0)
            FROM books b;
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return new ReadingListCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
    }

    /// <summary>The summaries with their own and their series' flags filled in (two small queries for a whole page).</summary>
    internal static List<BookSummary> Decorate(SqliteConnection conn, IReadOnlyList<BookSummary> items)
    {
        if (items.Count == 0) return [];
        var own = new Dictionary<string, (bool Favorite, string Status)>(StringComparer.Ordinal);
        var series = new Dictionary<string, (bool Favorite, string Status)>(StringComparer.Ordinal);

        using (var cmd = conn.CreateCommand())
        {
            var names = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                string p = "$b" + i;
                names.Add(p);
                cmd.Parameters.AddWithValue(p, items[i].BookId);
            }
            cmd.CommandText = $"SELECT book_id, is_favorite, reading_status FROM books WHERE book_id IN ({string.Join(",", names)});";
            using var r = cmd.ExecuteReader();
            while (r.Read()) own[r.GetString(0)] = (r.GetInt32(1) != 0, r.GetString(2));
        }

        var seriesNames = items.Select(i => i.SeriesName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
        if (seriesNames.Count > 0)
        {
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < seriesNames.Count; i++)
            {
                string p = "$s" + i;
                names.Add(p);
                cmd.Parameters.AddWithValue(p, seriesNames[i]);
            }
            cmd.CommandText = $"SELECT series_name, is_favorite, reading_status FROM series_flags WHERE series_name IN ({string.Join(",", names)});";
            using var r = cmd.ExecuteReader();
            while (r.Read()) series[r.GetString(0)] = (r.GetInt32(1) != 0, r.GetString(2));
        }

        return items.Select(i =>
        {
            var o = own.GetValueOrDefault(i.BookId);
            var s = series.GetValueOrDefault(i.SeriesName);
            return i with { IsFavorite = o.Favorite, ReadingStatus = o.Status ?? "", SeriesIsFavorite = s.Favorite, SeriesReadingStatus = s.Status ?? "" };
        }).ToList();
    }
}

public static class ReadingStatus
{
    public const string Reading = "reading", Planned = "planned", Completed = "completed";

    /// <summary>Null stays null (leave as is), "" clears, a known status is kept; anything else is an error.</summary>
    public static string? Normalize(string? status)
    {
        if (status is null) return null;
        string s = status.Trim().ToLowerInvariant();
        return s is "" or Reading or Planned or Completed
            ? s
            : throw new ArgumentException($"Unknown reading status '{status}' (use reading, planned, completed, or empty).");
    }
}
