using System.Text.Json;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.CustomFields;
using ImmersiveReading.Library.Internal;
using ImmersiveReading.Library.Tags;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

public sealed class BookMetadataService
{
    private readonly LibraryDatabase _db;
    private readonly BookQueryService _bookQueryService;
    private readonly TagService _tagService;
    private readonly CustomFieldService _customFieldService;
    private readonly TimeProvider _timeProvider;

    public BookMetadataService(
        LibraryDatabase db,
        BookQueryService bookQueryService,
        TagService tagService,
        CustomFieldService customFieldService,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _bookQueryService = bookQueryService;
        _tagService = tagService;
        _customFieldService = customFieldService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public BookDetail UpdateBookMetadata(BookMetadataUpdateRequest req)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE books
                SET title = COALESCE($title, title),
                    sort_title = COALESCE($sortTitle, sort_title),
                    authors = COALESCE($authors, authors),
                    author_sort = COALESCE($authorSort, author_sort),
                    isbn = COALESCE($isbn, isbn),
                    publisher = COALESCE($publisher, publisher),
                    imprint = COALESCE($imprint, imprint),
                    published_date = COALESCE($pubDate, published_date),
                    series_name = COALESCE($seriesName, series_name),
                    series_index = CASE WHEN $clearSeriesIdx = 1 THEN NULL ELSE COALESCE($seriesIdx, series_index) END,
                    description = COALESCE($desc, description),
                    language = COALESCE($lang, language),
                    writing_mode = COALESCE($writingMode, writing_mode),
                    folder_id = CASE WHEN $clearFolder = 1 THEN NULL ELSE COALESCE($folderId, folder_id) END,
                    custom_sort_order = COALESCE($customSort, custom_sort_order),
                    is_metadata_edited = 1,
                    updated_at_utc = $now
                WHERE book_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", req.BookId);
            cmd.Parameters.AddWithValue("$title", (object?)req.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sortTitle", (object?)req.SortTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authors", (object?)req.Authors ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorSort", (object?)req.AuthorSort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isbn", (object?)req.Isbn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$publisher", (object?)req.Publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imprint", (object?)req.Imprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pubDate", (object?)req.PublishedDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seriesName", (object?)req.SeriesName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seriesIdx", (object?)req.SeriesIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clearSeriesIdx", req.ClearSeriesIndex ? 1 : 0);
            cmd.Parameters.AddWithValue("$desc", (object?)req.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lang", (object?)req.Language ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$writingMode", (object?)req.WritingMode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$folderId", (object?)req.FolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clearFolder", req.ClearFolderId ? 1 : 0);
            cmd.Parameters.AddWithValue("$customSort", (object?)req.CustomSortOrder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            int affected = cmd.ExecuteNonQuery();
            if (affected == 0)
            {
                throw new KeyNotFoundException($"Book '{req.BookId}' not found.");
            }
        }

        if (req.TagIds is not null)
        {
            _tagService.SetBookTagsInTransaction(conn, tx, req.BookId, req.TagIds);
        }

        if (req.CustomFields is not null)
        {
            _customFieldService.SetEntityCustomFieldsInTransaction(conn, tx, "book", req.BookId, req.CustomFields);
        }

        FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, req.BookId);
        tx.Commit();

        return _bookQueryService.GetBookDetail(req.BookId)!;
    }

    public int BulkUpdateBooks(BulkBookUpdatePatch patch)
    {
        if (patch.BookIds is null || patch.BookIds.Count == 0) return 0;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        int updated = 0;

        for (int i = 0; i < patch.BookIds.Count; i++)
        {
            string bookId = patch.BookIds[i];
            double? volumeNumber = patch.AutoNumberVolumes ? (patch.AutoNumberStart + i) : null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE books
                    SET folder_id = CASE WHEN $clearFolder = 1 THEN NULL ELSE COALESCE($folderId, folder_id) END,
                        series_name = COALESCE($seriesName, series_name),
                        series_index = COALESCE($volNum, series_index),
                        authors = COALESCE($authors, authors),
                        author_sort = COALESCE($authorSort, author_sort),
                        publisher = COALESCE($publisher, publisher),
                        imprint = COALESCE($imprint, imprint),
                        language = COALESCE($lang, language),
                        is_metadata_edited = 1,
                        updated_at_utc = $now
                    WHERE book_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", bookId);
                cmd.Parameters.AddWithValue("$clearFolder", patch.ClearFolderId ? 1 : 0);
                cmd.Parameters.AddWithValue("$folderId", (object?)patch.FolderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seriesName", (object?)patch.SeriesName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$volNum", (object?)volumeNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$authors", (object?)patch.Authors ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$authorSort", (object?)patch.AuthorSort ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$publisher", (object?)patch.Publisher ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$imprint", (object?)patch.Imprint ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lang", (object?)patch.Language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                updated += cmd.ExecuteNonQuery();
            }

            if (patch.AddTagIds is { Count: > 0 })
            {
                foreach (string tagId in patch.AddTagIds)
                {
                    using var tCmd = conn.CreateCommand();
                    tCmd.Transaction = tx;
                    tCmd.CommandText = "INSERT OR IGNORE INTO book_tags (book_id, tag_id, assigned_at_utc) VALUES ($b, $t, $now);";
                    tCmd.Parameters.AddWithValue("$b", bookId);
                    tCmd.Parameters.AddWithValue("$t", tagId);
                    tCmd.Parameters.AddWithValue("$now", now);
                    tCmd.ExecuteNonQuery();
                }
            }

            if (patch.RemoveTagIds is { Count: > 0 })
            {
                foreach (string tagId in patch.RemoveTagIds)
                {
                    using var tCmd = conn.CreateCommand();
                    tCmd.Transaction = tx;
                    tCmd.CommandText = "DELETE FROM book_tags WHERE book_id = $b AND tag_id = $t;";
                    tCmd.Parameters.AddWithValue("$b", bookId);
                    tCmd.Parameters.AddWithValue("$t", tagId);
                    tCmd.ExecuteNonQuery();
                }
            }

            if (patch.CustomFields is { Count: > 0 })
            {
                _customFieldService.SetEntityCustomFieldsInTransaction(conn, tx, "book", bookId, patch.CustomFields);
            }

            FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, bookId);
        }

        tx.Commit();
        return updated;
    }

    public BookDetail ResetBookMetadataToOpf(string bookId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        string? opfJson = null;
        using (var getCmd = conn.CreateCommand())
        {
            getCmd.Transaction = tx;
            getCmd.CommandText = "SELECT opf_original_json FROM books WHERE book_id = $id;";
            getCmd.Parameters.AddWithValue("$id", bookId);
            opfJson = getCmd.ExecuteScalar() as string;
        }

        if (opfJson is null)
        {
            throw new KeyNotFoundException($"Book '{bookId}' not found.");
        }

        var opf = JsonSerializer.Deserialize<OpfOriginalMetadata>(opfJson) ?? new OpfOriginalMetadata();
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE books
                SET title = $title,
                    sort_title = $sortTitle,
                    authors = $authors,
                    author_sort = $authorSort,
                    isbn = $isbn,
                    publisher = $publisher,
                    imprint = $imprint,
                    published_date = $pubDate,
                    language = $lang,
                    description = $desc,
                    series_name = $seriesName,
                    series_index = $seriesIdx,
                    writing_mode = $writingMode,
                    is_metadata_edited = 0,
                    updated_at_utc = $now
                WHERE book_id = $id;
                """;
            upd.Parameters.AddWithValue("$id", bookId);
            upd.Parameters.AddWithValue("$title", opf.Title);
            upd.Parameters.AddWithValue("$sortTitle", !string.IsNullOrWhiteSpace(opf.SortTitle) ? opf.SortTitle : opf.Title);
            upd.Parameters.AddWithValue("$authors", opf.Authors);
            upd.Parameters.AddWithValue("$authorSort", !string.IsNullOrWhiteSpace(opf.AuthorSort) ? opf.AuthorSort : opf.Authors);
            upd.Parameters.AddWithValue("$isbn", opf.Isbn);
            upd.Parameters.AddWithValue("$publisher", opf.Publisher);
            upd.Parameters.AddWithValue("$imprint", opf.Imprint);
            upd.Parameters.AddWithValue("$pubDate", opf.PublishedDate);
            upd.Parameters.AddWithValue("$lang", opf.Language);
            upd.Parameters.AddWithValue("$desc", opf.Description);
            upd.Parameters.AddWithValue("$seriesName", opf.SeriesName);
            upd.Parameters.AddWithValue("$seriesIdx", (object?)opf.SeriesIndex ?? DBNull.Value);
            upd.Parameters.AddWithValue("$writingMode", opf.WritingMode);
            upd.Parameters.AddWithValue("$now", now);
            upd.ExecuteNonQuery();
        }

        FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, bookId);
        tx.Commit();

        return _bookQueryService.GetBookDetail(bookId)!;
    }

    public void DeleteBook(string bookId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var delFts = conn.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText = "DELETE FROM books_fts WHERE book_id = $id;";
            delFts.Parameters.AddWithValue("$id", bookId);
            delFts.ExecuteNonQuery();
        }

        using (var delCf = conn.CreateCommand())
        {
            delCf.Transaction = tx;
            delCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $id;";
            delCf.Parameters.AddWithValue("$id", bookId);
            delCf.ExecuteNonQuery();
        }

        using (var delB = conn.CreateCommand())
        {
            delB.Transaction = tx;
            delB.CommandText = "DELETE FROM books WHERE book_id = $id;";
            delB.Parameters.AddWithValue("$id", bookId);
            delB.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int BulkDeleteBooks(IReadOnlyList<string> bookIds)
    {
        if (bookIds is null || bookIds.Count == 0) return 0;
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        int count = 0;
        foreach (string bookId in bookIds)
        {
            using (var delFts = conn.CreateCommand())
            {
                delFts.Transaction = tx;
                delFts.CommandText = "DELETE FROM books_fts WHERE book_id = $id;";
                delFts.Parameters.AddWithValue("$id", bookId);
                delFts.ExecuteNonQuery();
            }

            using (var delCf = conn.CreateCommand())
            {
                delCf.Transaction = tx;
                delCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $id;";
                delCf.Parameters.AddWithValue("$id", bookId);
                delCf.ExecuteNonQuery();
            }

            using (var delB = conn.CreateCommand())
            {
                delB.Transaction = tx;
                delB.CommandText = "DELETE FROM books WHERE book_id = $id;";
                delB.Parameters.AddWithValue("$id", bookId);
                count += delB.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return count;
    }

    public void ReorderBooks(IReadOnlyList<string> orderedBookIds)
    {
        if (orderedBookIds is null || orderedBookIds.Count == 0) return;
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");

        for (int i = 0; i < orderedBookIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE books SET custom_sort_order = $order, updated_at_utc = $now WHERE book_id = $id;";
            cmd.Parameters.AddWithValue("$order", i + 1);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", orderedBookIds[i]);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public ClearLibraryResult ClearLibrary(ClearLibraryOptions? options = null)
    {
        var opt = options ?? new ClearLibraryOptions();
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        int booksCount;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.Transaction = tx;
            countCmd.CommandText = "SELECT COUNT(*) FROM books;";
            booksCount = Convert.ToInt32(countCmd.ExecuteScalar());
        }

        using (var delFts = conn.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText = "DELETE FROM books_fts;";
            delFts.ExecuteNonQuery();
        }

        using (var delCf = conn.CreateCommand())
        {
            delCf.Transaction = tx;
            delCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'book';";
            delCf.ExecuteNonQuery();
        }

        using (var delCompQ = conn.CreateCommand())
        {
            delCompQ.Transaction = tx;
            delCompQ.CommandText = "DELETE FROM comprehension_questions;";
            delCompQ.ExecuteNonQuery();
        }

        using (var delCompS = conn.CreateCommand())
        {
            delCompS.Transaction = tx;
            delCompS.CommandText = "DELETE FROM comprehension_sessions;";
            delCompS.ExecuteNonQuery();
        }

        using (var delChatMsg = conn.CreateCommand())
        {
            delChatMsg.Transaction = tx;
            delChatMsg.CommandText = "DELETE FROM persona_chat_messages;";
            delChatMsg.ExecuteNonQuery();
        }

        using (var delChatSess = conn.CreateCommand())
        {
            delChatSess.Transaction = tx;
            delChatSess.CommandText = "DELETE FROM persona_chat_sessions;";
            delChatSess.ExecuteNonQuery();
        }

        using (var delPersonas = conn.CreateCommand())
        {
            delPersonas.Transaction = tx;
            delPersonas.CommandText = "DELETE FROM book_personas;";
            delPersonas.ExecuteNonQuery();
        }

        using (var delBooks = conn.CreateCommand())
        {
            delBooks.Transaction = tx;
            delBooks.CommandText = "DELETE FROM books;";
            delBooks.ExecuteNonQuery();
        }

        int foldersCount = 0;
        if (opt.RemoveFolders)
        {
            using (var countFCmd = conn.CreateCommand())
            {
                countFCmd.Transaction = tx;
                countFCmd.CommandText = "SELECT COUNT(*) FROM library_folders;";
                foldersCount = Convert.ToInt32(countFCmd.ExecuteScalar());
            }

            using (var delFolderCf = conn.CreateCommand())
            {
                delFolderCf.Transaction = tx;
                delFolderCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'folder';";
                delFolderCf.ExecuteNonQuery();
            }

            using (var delFolderTags = conn.CreateCommand())
            {
                delFolderTags.Transaction = tx;
                delFolderTags.CommandText = "DELETE FROM folder_tags;";
                delFolderTags.ExecuteNonQuery();
            }

            using (var delFolders = conn.CreateCommand())
            {
                delFolders.Transaction = tx;
                delFolders.CommandText = "DELETE FROM library_folders;";
                delFolders.ExecuteNonQuery();
            }
        }

        if (opt.ResetVocabulary)
        {
            using (var delVocab = conn.CreateCommand())
            {
                delVocab.Transaction = tx;
                delVocab.CommandText = "DELETE FROM vocabulary_state;";
                delVocab.ExecuteNonQuery();
            }
        }

        tx.Commit();

        int coversDeleted = 0;
        if (Directory.Exists(_db.CoversCacheDirectory))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_db.CoversCacheDirectory))
                {
                    try
                    {
                        File.Delete(file);
                        coversDeleted++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        return new ClearLibraryResult(booksCount, foldersCount, coversDeleted, opt.ResetVocabulary);
    }
}

