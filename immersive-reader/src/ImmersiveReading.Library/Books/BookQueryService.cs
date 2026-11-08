using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.CustomFields;
using ImmersiveReading.Library.Folders;
using ImmersiveReading.Library.Tags;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

public sealed class BookQueryService : IBookCatalog
{
    private readonly LibraryDatabase _db;
    private readonly TagService _tagService;
    private readonly CustomFieldService _customFieldService;

    public BookQueryService(
        LibraryDatabase db,
        TagService tagService,
        CustomFieldService customFieldService)
    {
        _db = db;
        _tagService = tagService;
        _customFieldService = customFieldService;
    }

    public PagedBookListResult ListBooks(BookListQuery query)
    {
        var sw = Stopwatch.StartNew();
        using var conn = _db.OpenConnection();

        var rawFolders = FolderService.LoadAllFoldersRaw(conn);
        var folderLookup = rawFolders.ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = CustomFieldService.LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = CustomFieldService.LoadAllCustomFieldValues(conn);

        var (whereSql, orderBySql, parameters) = BookListSqlBuilder.Build(query, rawFolders);

        // Count total matching rows
        int totalCount;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM books b {whereSql};";
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        int safeLimit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 1000);
        int safeOffset = Math.Max(0, query.Offset);

        var items = new List<BookSummary>();
        using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = $"""
                SELECT b.book_id, b.epub_sha256, b.folder_id, b.title, b.sort_title,
                       b.series_name, b.series_index, b.custom_sort_order, b.authors, b.author_sort,
                       b.isbn, b.publisher, b.imprint, b.published_date, b.language, b.description,
                       b.is_metadata_edited, b.folder_path, b.epub_file_name, b.file_size_bytes,
                       b.cover_cache_path, b.chapter_count, b.total_char_count, b.stats_computed,
                       b.writing_mode, b.added_at_utc, b.updated_at_utc, b.last_opened_at_utc,
                       b.has_study_pack, COALESCE(rp.overall_completion, 0.0)
                FROM books b
                LEFT JOIN reading_progress rp ON rp.book_id = b.book_id
                {whereSql}
                {orderBySql}
                LIMIT $limit OFFSET $offset;
                """;
            foreach (var p in parameters)
            {
                listCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            listCmd.Parameters.AddWithValue("$limit", safeLimit);
            listCmd.Parameters.AddWithValue("$offset", safeOffset);

            var rawRows = new List<(string BookId, Func<IReadOnlyList<TagRecord>, IReadOnlyDictionary<string, EffectiveCustomFieldValue>, BookSummary> Builder, string? FolderId, string Title)>();

            using (var r = listCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    rawRows.Add(BookSummaryReader.ReadRow(r, folderLookup));
                }
            }

            if (rawRows.Count > 0)
            {
                var pageBookIds = rawRows.Select(x => x.BookId).ToList();
                var tagsByBook = TagService.LoadTagsForBooks(conn, pageBookIds);

                foreach (var row in rawRows)
                {
                    var bTags = tagsByBook.GetValueOrDefault(row.BookId) ?? [];
                    var effFields = CustomFieldService.ResolveEffectiveCustomFieldsForBook(
                        row.BookId,
                        row.Title,
                        row.FolderId,
                        folderLookup,
                        fieldDefs,
                        allCustomValues);
                    var cfDict = effFields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);
                    items.Add(row.Builder(bTags, cfDict));
                }
            }
        }

        sw.Stop();
        return new PagedBookListResult(
            Items: items,
            TotalCount: totalCount,
            Offset: safeOffset,
            Limit: safeLimit,
            QueryElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2));
    }

    public BookSummary? GetBookSummary(string bookId)
    {
        using var conn = _db.OpenConnection();
        return ListBooksBySpecificId(conn, bookId);
    }

    public string? GetEpubFilePath(string bookId)
    {
        var summary = GetBookSummary(bookId);
        if (summary is null) return null;
        return Path.Combine(summary.FolderPath, summary.EpubFileName);
    }

    public BookDetail? GetBookDetail(string bookId)
    {
        using var conn = _db.OpenConnection();
        var page = ListBooksBySpecificId(conn, bookId);
        if (page is null) return null;

        var rawFolders = FolderService.LoadAllFoldersRaw(conn).ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = CustomFieldService.LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = CustomFieldService.LoadAllCustomFieldValues(conn);

        string opfJson = "{}";
        using (var opfCmd = conn.CreateCommand())
        {
            opfCmd.CommandText = "SELECT opf_original_json FROM books WHERE book_id = $id;";
            opfCmd.Parameters.AddWithValue("$id", bookId);
            if (opfCmd.ExecuteScalar() is string s) opfJson = s;
        }

        var opfOriginal = JsonSerializer.Deserialize<OpfOriginalMetadata>(opfJson) ?? new OpfOriginalMetadata();

        var chapters = new List<BookChapterRecord>();
        using (var chCmd = conn.CreateCommand())
        {
            chCmd.CommandText = """
                SELECT chapter_index, chapter_id, title, spine_href, fragment_id,
                       end_spine_href, end_fragment_id, char_count, cumulative_chars, is_excluded
                FROM book_chapters
                WHERE book_id = $id
                ORDER BY chapter_index ASC;
                """;
            chCmd.Parameters.AddWithValue("$id", bookId);
            using var r = chCmd.ExecuteReader();
            while (r.Read())
            {
                chapters.Add(new BookChapterRecord(
                    BookId: bookId,
                    ChapterIndex: r.GetInt32(0),
                    ChapterId: r.GetString(1),
                    Title: r.GetString(2),
                    SpineHref: r.GetString(3),
                    FragmentId: r.GetString(4),
                    EndSpineHref: r.IsDBNull(5) ? null : r.GetString(5),
                    EndFragmentId: r.IsDBNull(6) ? null : r.GetString(6),
                    CharCount: r.GetInt32(7),
                    CumulativeChars: r.GetInt32(8),
                    IsExcluded: r.GetInt32(9) != 0));
            }
        }

        var effList = CustomFieldService.ResolveEffectiveCustomFieldsForBook(
            bookId,
            page.Title,
            page.FolderId,
            rawFolders,
            fieldDefs,
            allCustomValues);

        var directMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (allCustomValues.TryGetValue(("book", bookId), out var directVals))
        {
            foreach (var kv in directVals)
            {
                directMap[kv.Key] = kv.Value.ValueText;
            }
        }

        return new BookDetail(
            Summary: page,
            OpfOriginal: opfOriginal,
            Chapters: chapters,
            EffectiveCustomFields: effList,
            DirectBookCustomFields: directMap);
    }

    public BookSummary? ListBooksBySpecificId(SqliteConnection conn, string bookId)
    {
        var rawFolders = FolderService.LoadAllFoldersRaw(conn).ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = CustomFieldService.LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = CustomFieldService.LoadAllCustomFieldValues(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.book_id, b.epub_sha256, b.folder_id, b.title, b.sort_title,
                   b.series_name, b.series_index, b.custom_sort_order, b.authors, b.author_sort,
                   b.isbn, b.publisher, b.imprint, b.published_date, b.language, b.description,
                   b.is_metadata_edited, b.folder_path, b.epub_file_name, b.file_size_bytes,
                   b.cover_cache_path, b.chapter_count, b.total_char_count, b.stats_computed,
                   b.writing_mode, b.added_at_utc, b.updated_at_utc, b.last_opened_at_utc,
                   b.has_study_pack, COALESCE(rp.overall_completion, 0.0)
            FROM books b
            LEFT JOIN reading_progress rp ON rp.book_id = b.book_id
            WHERE b.book_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", bookId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        string? folderId = r.IsDBNull(2) ? null : r.GetString(2);
        string title = r.GetString(3);
        string? breadcrumb = FolderService.BuildFolderBreadcrumb(folderId, rawFolders);
        var tags = TagService.LoadTagsForBooks(conn, [bookId]).GetValueOrDefault(bookId) ?? [];
        var effFields = CustomFieldService.ResolveEffectiveCustomFieldsForBook(bookId, title, folderId, rawFolders, fieldDefs, allCustomValues);

        return new BookSummary(
            BookId: r.GetString(0),
            EpubSha256: r.GetString(1),
            FolderId: folderId,
            FolderPathBreadcrumb: breadcrumb,
            Title: title,
            SortTitle: r.GetString(4),
            SeriesName: r.GetString(5),
            SeriesIndex: r.IsDBNull(6) ? null : r.GetDouble(6),
            CustomSortOrder: r.GetInt32(7),
            Authors: r.GetString(8),
            AuthorSort: r.GetString(9),
            Isbn: r.GetString(10),
            Publisher: r.GetString(11),
            Imprint: r.GetString(12),
            PublishedDate: r.GetString(13),
            Language: r.GetString(14),
            Description: r.GetString(15),
            IsMetadataEdited: r.GetInt32(16) != 0,
            FolderPath: r.GetString(17),
            EpubFileName: r.GetString(18),
            FileSizeBytes: r.GetInt64(19),
            CoverCachePath: r.IsDBNull(20) ? null : r.GetString(20),
            ChapterCount: r.GetInt32(21),
            TotalCharCount: r.GetInt32(22),
            StatsComputed: r.GetInt32(23) != 0,
            WritingMode: r.GetString(24),
            AddedAtUtc: r.GetString(25),
            UpdatedAtUtc: r.GetString(26),
            LastOpenedAtUtc: r.IsDBNull(27) ? null : r.GetString(27),
            HasStudyPack: r.GetInt32(28) != 0,
            OverallCompletion: r.GetDouble(29),
            Tags: tags,
            CustomFields: effFields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase));
    }
}

