using ImmersiveReading.Library.Books;
using ImmersiveReading.Library.Internal;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Duplicates;

/// <summary>Database side of duplicate management (spec 21): load facts, search, resolve a group, remember decisions.</summary>
public sealed class DuplicateService(LibraryDatabase db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    // ------------------------------------------------------------------ search
    public DuplicateSearchResult FindDuplicates(DuplicateOptions? options = null)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var conn = db.OpenConnection();
        var facts = LoadFacts(conn);
        var dismissed = LoadDismissed(conn);
        var result = DuplicateDetector.Find(facts, dismissed, options);
        return result with { ElapsedMs = started.ElapsedMilliseconds };
    }

    private static List<BookFacts> LoadFacts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.book_id, b.title, b.series_name, b.series_index, b.authors, b.isbn, b.opf_identifier, b.language, b.publisher,
                   b.description, b.total_char_count, b.chapter_count, b.stats_computed, b.file_size_bytes, b.epub_file_name,
                   b.folder_path, b.added_at_utc, b.cover_cache_path IS NOT NULL, COALESCE(rp.overall_completion, 0), b.has_study_pack,
                   EXISTS (SELECT 1 FROM custom_field_values cv WHERE cv.entity_type = 'book' AND cv.entity_id = b.book_id
                           AND cv.field_key = 'vocab_deck_csv' AND cv.value_text != ''),
                   (SELECT COUNT(*) FROM book_tags bt WHERE bt.book_id = b.book_id)
            FROM books b LEFT JOIN reading_progress rp ON rp.book_id = b.book_id
            """;
        var list = new List<BookFacts>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BookFacts(
                r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12) != 0,
                r.GetInt64(13), r.GetString(14), r.GetString(15), r.GetString(16), r.GetInt32(17) != 0, r.GetDouble(18), r.GetInt32(19) != 0,
                r.GetInt32(20) != 0, r.GetInt32(21)));
        }
        return list;
    }

    private static HashSet<(string, string)> LoadDismissed(SqliteConnection conn)
    {
        var set = new HashSet<(string, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT book_a, book_b FROM duplicate_dismissals";
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add((r.GetString(0), r.GetString(1)));
        return set;
    }

    // ------------------------------------------------------------------ decisions
    /// <summary>"These are not duplicates": every pair of the group is remembered and never proposed again.</summary>
    public int DismissGroup(IReadOnlyList<string> bookIds)
    {
        var ids = bookIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count < 2) throw new ArgumentException("A group needs at least two books.");
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        RequireExisting(conn, tx, ids);

        int pairs = 0;
        string now = _time.GetUtcNow().ToString("O");
        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
            {
                var (a, b) = DuplicateDetector.PairKey(ids[i], ids[j]);
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO duplicate_dismissals(book_a, book_b, dismissed_at_utc) VALUES ($a, $b, $t)";
                cmd.Parameters.AddWithValue("$a", a);
                cmd.Parameters.AddWithValue("$b", b);
                cmd.Parameters.AddWithValue("$t", now);
                pairs += cmd.ExecuteNonQuery();
            }
        tx.Commit();
        return pairs;
    }

    /// <summary>
    /// Keeps one book and removes the others from the library (files untouched). Everything happens in one transaction; invalid
    /// input changes nothing. With <paramref name="mergeData"/> the kept book receives the removed books' tags, custom fields and
    /// any metadata it lacks.
    /// </summary>
    public ResolveResult Resolve(string keepBookId, IReadOnlyList<string> removeBookIds, bool mergeData = true)
    {
        var remove = removeBookIds.Distinct(StringComparer.Ordinal).ToList();
        if (remove.Count == 0) throw new ArgumentException("Choose at least one book to remove.");
        if (remove.Contains(keepBookId)) throw new ArgumentException("The book to keep cannot also be removed.");

        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        RequireExisting(conn, tx, remove.Prepend(keepBookId).ToList());

        int tags = 0, customFields = 0, fields = 0;
        string now = _time.GetUtcNow().ToString("O");

        if (mergeData)
        {
            foreach (string id in remove)
            {
                tags += Execute(conn, tx, """
                    INSERT OR IGNORE INTO book_tags(book_id, tag_id, assigned_at_utc)
                    SELECT $keep, tag_id, assigned_at_utc FROM book_tags WHERE book_id = $rm
                    """, ("$keep", keepBookId), ("$rm", id));

                customFields += Execute(conn, tx, """
                    INSERT INTO custom_field_values(entity_type, entity_id, field_key, value_text, value_number, updated_at_utc)
                    SELECT 'book', $keep, field_key, value_text, value_number, $now
                    FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $rm AND value_text != ''
                    ON CONFLICT(entity_type, entity_id, field_key) DO UPDATE
                        SET value_text = excluded.value_text, value_number = excluded.value_number, updated_at_utc = excluded.updated_at_utc
                        WHERE custom_field_values.value_text = ''
                    """, ("$keep", keepBookId), ("$rm", id), ("$now", now));
            }
            fields = FillEmptyFields(conn, tx, keepBookId, remove, now);
            if (tags + customFields + fields > 0) FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, keepBookId);
        }

        foreach (string id in remove)
        {
            Execute(conn, tx, """
                INSERT OR REPLACE INTO removed_books(book_id, epub_sha256, title, authors, epub_file_name, original_folder_path, kept_book_id, removed_at_utc)
                SELECT book_id, epub_sha256, title, authors, epub_file_name, folder_path, $keep, $now FROM books WHERE book_id = $rm
                """, ("$keep", keepBookId), ("$rm", id), ("$now", now));
            BookMetadataService.DeleteBookInTransaction(conn, tx, id);
        }

        tx.Commit();
        return new ResolveResult(remove.Count, tags, customFields, fields);
    }

    /// <summary>
    /// Mass clear: resolves many groups, each in its own transaction (so one stale group cannot undo or block the others). A group
    /// that fails validation is reported in <see cref="BatchResolveResult.Failed"/> and the rest continue.
    /// </summary>
    public BatchResolveResult ResolveMany(IReadOnlyList<ResolveGroupRequest> groups, bool mergeData = true)
    {
        int resolved = 0, removed = 0, tags = 0, customFields = 0, fields = 0;
        var done = new List<string>();
        var failed = new List<ResolveGroupFailure>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in groups)
        {
            try
            {
                // groups must be disjoint: a book already used by an earlier group of this call is a client error
                var ids = group.RemoveBookIds.Append(group.KeepBookId).ToList();
                if (ids.Any(id => !used.Add(id))) throw new ArgumentException("A book appears in more than one group of the same request.");

                var r = Resolve(group.KeepBookId, group.RemoveBookIds, mergeData);
                resolved++;
                removed += r.Removed;
                tags += r.TagsMerged;
                customFields += r.CustomFieldsMerged;
                fields += r.FieldsFilled;
                done.Add(group.KeepBookId);
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
            {
                failed.Add(new ResolveGroupFailure(group.KeepBookId, ex.Message));
            }
        }
        return new BatchResolveResult(resolved, removed, tags, customFields, fields, done, failed);
    }

    private static int FillEmptyFields(SqliteConnection conn, SqliteTransaction tx, string keepId, List<string> removeIds, string now)
    {
        var keep = ReadMetadata(conn, tx, keepId);
        var donors = removeIds.Select(id => ReadMetadata(conn, tx, id)).ToList();
        int filled = 0;
        var updates = new List<(string Column, object Value)>();

        void Fill(string column, string current, Func<Metadata, string> pick)
        {
            if (!string.IsNullOrWhiteSpace(current)) return;
            string? value = donors.Select(pick).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (value is not null) updates.Add((column, value));
        }

        // Series name and index belong together: take both from one donor, or only the index when the series already matches.
        if (string.IsNullOrWhiteSpace(keep.SeriesName))
        {
            var donor = donors.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.SeriesName));
            if (donor is not null)
            {
                updates.Add(("series_name", donor.SeriesName));
                if (donor.SeriesIndex is { } idx) updates.Add(("series_index", idx));
            }
        }
        else if (keep.SeriesIndex is null)
        {
            var donor = donors.FirstOrDefault(d => d.SeriesIndex is not null && DuplicateNormalizer.Series(d.SeriesName) == DuplicateNormalizer.Series(keep.SeriesName));
            if (donor?.SeriesIndex is { } idx) updates.Add(("series_index", idx));
        }
        if (string.IsNullOrWhiteSpace(keep.Authors))
        {
            var donor = donors.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Authors));
            if (donor is not null)
            {
                updates.Add(("authors", donor.Authors));
                if (string.IsNullOrWhiteSpace(keep.AuthorSort)) updates.Add(("author_sort", string.IsNullOrWhiteSpace(donor.AuthorSort) ? donor.Authors : donor.AuthorSort));
            }
        }
        Fill("isbn", keep.Isbn, d => d.Isbn);
        Fill("publisher", keep.Publisher, d => d.Publisher);
        Fill("description", keep.Description, d => d.Description);

        foreach (var (column, value) in updates)
        {
            // column names come from the fixed list above, never from input
            Execute(conn, tx, $"UPDATE books SET {column} = $v, updated_at_utc = $now WHERE book_id = $id",
                ("$v", value), ("$now", now), ("$id", keepId));
            filled++;
        }
        return filled;
    }

    private sealed record Metadata(string SeriesName, double? SeriesIndex, string Authors, string AuthorSort, string Isbn, string Publisher, string Description);

    private static Metadata ReadMetadata(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT series_name, series_index, authors, author_sort, isbn, publisher, description FROM books WHERE book_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        r.Read();
        return new Metadata(r.GetString(0), r.IsDBNull(1) ? null : r.GetDouble(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));
    }

    private static void RequireExisting(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<string> ids)
    {
        foreach (string id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM books WHERE book_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0) throw new KeyNotFoundException($"Book '{id}' is not in the library (anymore). Search again.");
        }
    }

    private static int Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ removed books
    public IReadOnlyList<RemovedBook> ListRemoved()
    {
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT book_id, title, authors, epub_file_name, original_folder_path, kept_book_id, removed_at_utc FROM removed_books ORDER BY removed_at_utc DESC";
        var list = new List<RemovedBook>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new RemovedBook(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    /// <summary>Allows the next scan to import the file again.</summary>
    public bool ForgetRemoved(string bookId)
    {
        using var conn = db.OpenConnection();
        using var tx = conn.BeginTransaction();
        int n = Execute(conn, tx, "DELETE FROM removed_books WHERE book_id = $id", ("$id", bookId));
        tx.Commit();
        return n > 0;
    }
}
