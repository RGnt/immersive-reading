using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Tests.Epub;
using ImmersiveReading.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImmersiveReading.Core.Tests.Books;

/// <summary>Spec 12 FR-10/FR-11: legacy books are rebuilt lazily and saved positions are remapped.</summary>
public sealed class ChapterModelUpgradeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public ChapterModelUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-chapter-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _manager = new LibraryManager(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* Windows file locks */ }
    }

    private static readonly GroupingPage[] Spine =
    [
        GroupingEpubFactory.Text("text/c1_000.xhtml", "第1話"),
        GroupingEpubFactory.Text("text/c1_001.xhtml"),
        GroupingEpubFactory.Illustration("text/img1.xhtml"),
        GroupingEpubFactory.Text("text/c2_000.xhtml", "第2話"),
        GroupingEpubFactory.Text("text/c2_001.xhtml"),
    ];

    private async Task<(string BookId, string EpubPath, BookDetail Modern)> ImportAsync()
    {
        string epubPath = Path.Combine(_tempDir, $"book-{Guid.NewGuid():N}.epub");
        await File.WriteAllBytesAsync(epubPath, GroupingEpubFactory.BuildBytes(Spine));
        var detail = await _manager.ImportBookAsync(epubPath);
        return (detail.Summary.BookId, epubPath, detail);
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    /// <summary>Rewrites the stored rows to the legacy shape: one record per spine file, version 1.</summary>
    private List<string> MakeLegacy(string bookId, BookDetail modern)
    {
        var hrefs = modern.Chapters.SelectMany(c => c.Members).ToList();
        Exec("DELETE FROM book_chapters WHERE book_id = $id;", ("$id", bookId));
        for (int i = 0; i < hrefs.Count; i++)
        {
            Exec("""
                INSERT INTO book_chapters (book_id, chapter_index, chapter_id, title, spine_href, fragment_id, char_count, cumulative_chars, is_excluded, member_spine_hrefs)
                VALUES ($id, $i, $cid, $title, $href, '', 0, 0, 0, '[]');
                """,
                ("$id", bookId), ("$i", i), ("$cid", $"legacy-{i}"), ("$title", $"legacy {i}"), ("$href", hrefs[i]));
        }
        Exec("UPDATE books SET chapter_model_version = 1, chapter_count = $n WHERE book_id = $id;", ("$n", hrefs.Count), ("$id", bookId));
        return hrefs;
    }

    private void SetProgress(string bookId, int chapterIndex, string href, int? token, int offset) =>
        Exec("""
            INSERT INTO reading_progress (book_id, current_chapter_index, current_spine_href, locator_token_id, locator_char_offset, scroll_or_page_ratio, overall_completion, updated_at_utc)
            VALUES ($id, $ci, $href, $tok, $off, 0.5, 0.25, '2026-01-01T00:00:00Z');
            """,
            ("$id", bookId), ("$ci", chapterIndex), ("$href", href), ("$tok", token), ("$off", offset));

    private void AddBookmark(string bookId, string id, int chapterIndex, string href, int token, int offset) =>
        Exec("""
            INSERT INTO bookmarks (bookmark_id, book_id, chapter_index, spine_href, start_token_id, end_token_id, start_char_offset, end_char_offset, selected_text, user_note, created_at_utc)
            VALUES ($bm, $id, $ci, $href, $tok, $tok, $off, $off, 'selected text', 'my note', '2026-01-01T00:00:00Z');
            """,
            ("$bm", id), ("$id", bookId), ("$ci", chapterIndex), ("$href", href), ("$tok", token), ("$off", offset));

    [Fact]
    public void InitializeSchema_AddsNewColumnsToADatabaseThatPredatesThem_AndIsIdempotent()
    {
        // Simulate a library DB created before spec 12 by removing the two columns again.
        Exec("ALTER TABLE books DROP COLUMN chapter_model_version;");
        Exec("ALTER TABLE book_chapters DROP COLUMN member_spine_hrefs;");

        _db.InitializeSchema();
        _db.InitializeSchema();   // second run must be a no-op, not a duplicate-column error

        Exec("""
            INSERT INTO books (book_id, epub_sha256, title, sort_title, folder_path, epub_file_name, added_at_utc, updated_at_utc)
            VALUES ('legacy', 'sha', 'T', 'T', '.', 'x.epub', '2026-01-01', '2026-01-01');
            """);
        Assert.Equal(1, Scalar<int>("SELECT chapter_model_version FROM books WHERE book_id = 'legacy';"));   // default = legacy model
        Exec("""
            INSERT INTO book_chapters (book_id, chapter_index, chapter_id, title, spine_href)
            VALUES ('legacy', 0, 'c', 'T', 'a.xhtml');
            """);
        Assert.Equal("[]", Scalar<string>("SELECT member_spine_hrefs FROM book_chapters WHERE book_id = 'legacy';"));
    }

    [Fact]
    public async Task NewImport_IsCurrentVersion_AndPersistsMembers()
    {
        var (bookId, _, modern) = await ImportAsync();

        Assert.Equal(2, Scalar<int>("SELECT chapter_model_version FROM books WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(2, modern.Chapters.Count);
        Assert.Equal(3, modern.Chapters[0].Members.Count);   // text, text, illustration

        var reread = _manager.GetBookDetail(bookId)!;
        Assert.Equal(modern.Chapters.Select(c => c.Members.Count), reread.Chapters.Select(c => c.Members.Count));
        Assert.Equal(modern.Chapters[0].EndSpineHref, reread.Chapters[0].EndSpineHref);
    }

    [Fact]
    public async Task LegacyBook_IsRebuiltOnFirstRead_AndVersionIsSet()
    {
        var (bookId, _, modern) = await ImportAsync();
        var legacyHrefs = MakeLegacy(bookId, modern);
        Assert.Equal(5, legacyHrefs.Count);

        var detail = _manager.GetBookDetail(bookId)!;

        Assert.Equal(2, detail.Chapters.Count);
        Assert.DoesNotContain(detail.Chapters, c => c.Title.StartsWith("legacy"));
        Assert.Equal(2, Scalar<int>("SELECT chapter_model_version FROM books WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(2, Scalar<int>("SELECT chapter_count FROM books WHERE book_id = $id;", ("$id", bookId)));
    }

    [Fact]
    public async Task Progress_InPrimaryFile_IsPreservedExactly_WithRemappedChapterIndex()
    {
        var (bookId, _, modern) = await ImportAsync();
        var hrefs = MakeLegacy(bookId, modern);
        // Legacy index 3 = c2_000 (primary of chapter 2); legacy chapter index in the old list was 3.
        SetProgress(bookId, 3, hrefs[3], token: 42, offset: 120);

        _manager.GetBookDetail(bookId);

        Assert.Equal(1, Scalar<int>("SELECT current_chapter_index FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(hrefs[3], Scalar<string>("SELECT current_spine_href FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(42, Scalar<int>("SELECT locator_token_id FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(120, Scalar<int>("SELECT locator_char_offset FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
    }

    [Fact]
    public async Task Progress_InContinuationFile_DegradesToChapterStart()
    {
        var (bookId, _, modern) = await ImportAsync();
        var hrefs = MakeLegacy(bookId, modern);
        // Legacy index 1 = c1_001, a continuation member of chapter 1.
        SetProgress(bookId, 1, hrefs[1], token: 77, offset: 300);

        _manager.GetBookDetail(bookId);

        Assert.Equal(0, Scalar<int>("SELECT current_chapter_index FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(hrefs[0], Scalar<string>("SELECT current_spine_href FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(0, Scalar<int>("SELECT locator_char_offset FROM reading_progress WHERE book_id = $id;", ("$id", bookId)));
        Assert.Equal(0, Scalar<int>("SELECT COUNT(*) FROM reading_progress WHERE book_id = $id AND locator_token_id IS NOT NULL;", ("$id", bookId)));
    }

    [Fact]
    public async Task Bookmarks_AreRemapped_AndContinuationOnesKeepTextAndNote()
    {
        var (bookId, _, modern) = await ImportAsync();
        var hrefs = MakeLegacy(bookId, modern);
        AddBookmark(bookId, "bm-primary", 3, hrefs[3], token: 9, offset: 50);
        AddBookmark(bookId, "bm-member", 4, hrefs[4], token: 11, offset: 80);

        _manager.GetBookDetail(bookId);

        Assert.Equal(1, Scalar<int>("SELECT chapter_index FROM bookmarks WHERE bookmark_id = 'bm-primary';"));
        Assert.Equal(50, Scalar<int>("SELECT start_char_offset FROM bookmarks WHERE bookmark_id = 'bm-primary';"));

        Assert.Equal(1, Scalar<int>("SELECT chapter_index FROM bookmarks WHERE bookmark_id = 'bm-member';"));
        Assert.Equal(hrefs[3], Scalar<string>("SELECT spine_href FROM bookmarks WHERE bookmark_id = 'bm-member';"));
        Assert.Equal(0, Scalar<int>("SELECT start_char_offset FROM bookmarks WHERE bookmark_id = 'bm-member';"));
        Assert.Equal("selected text", Scalar<string>("SELECT selected_text FROM bookmarks WHERE bookmark_id = 'bm-member';"));
        Assert.Equal("my note", Scalar<string>("SELECT user_note FROM bookmarks WHERE bookmark_id = 'bm-member';"));
    }

    [Fact]
    public async Task QuizSessions_AreRemappedByTheirOldChapterHref()
    {
        var (bookId, _, modern) = await ImportAsync();
        MakeLegacy(bookId, modern);
        Exec("""
            INSERT INTO comprehension_sessions (session_id, book_id, chapter_index, start_token_id, end_token_id, passage_text, created_at_utc)
            VALUES ('qs-1', $id, 4, 0, 5, 'text', '2026-01-01T00:00:00Z');
            """, ("$id", bookId));

        _manager.GetBookDetail(bookId);

        Assert.Equal(1, Scalar<int>("SELECT chapter_index FROM comprehension_sessions WHERE session_id = 'qs-1';"));
    }

    [Fact]
    public async Task Upgrade_RunsOnce_AndLaterReadsDoNotRebuild()
    {
        var (bookId, _, modern) = await ImportAsync();
        MakeLegacy(bookId, modern);
        _manager.GetBookDetail(bookId);

        Exec("UPDATE book_chapters SET title = 'edited after upgrade' WHERE book_id = $id AND chapter_index = 0;", ("$id", bookId));
        var again = _manager.GetBookDetail(bookId)!;

        Assert.Equal("edited after upgrade", again.Chapters[0].Title);
    }

    [Fact]
    public async Task MissingEpub_LeavesTheLegacyBookUntouched()
    {
        var (bookId, epubPath, modern) = await ImportAsync();
        MakeLegacy(bookId, modern);
        string stored = Path.Combine(
            Scalar<string>("SELECT folder_path FROM books WHERE book_id = $id;", ("$id", bookId)),
            Scalar<string>("SELECT epub_file_name FROM books WHERE book_id = $id;", ("$id", bookId)));
        File.Delete(stored);
        if (File.Exists(epubPath)) File.Delete(epubPath);

        var detail = _manager.GetBookDetail(bookId)!;

        Assert.Equal(5, detail.Chapters.Count);                      // old list still served
        Assert.Equal(1, Scalar<int>("SELECT chapter_model_version FROM books WHERE book_id = $id;", ("$id", bookId)));
    }

    [Fact]
    public async Task ConcurrentFirstReads_UpgradeOnce_WithoutErrors()
    {
        var (bookId, _, modern) = await ImportAsync();
        MakeLegacy(bookId, modern);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _manager.GetBookDetail(bookId)!)));

        Assert.All(results, d => Assert.Equal(2, d.Chapters.Count));
        Assert.Equal(2, Scalar<int>("SELECT COUNT(*) FROM book_chapters WHERE book_id = $id;", ("$id", bookId)));
    }
}
