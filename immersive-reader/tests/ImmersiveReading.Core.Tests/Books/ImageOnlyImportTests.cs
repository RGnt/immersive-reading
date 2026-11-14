using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Tests.Epub;
using ImmersiveReading.Library;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImmersiveReading.Core.Tests.Books;

/// <summary>Spec 13: image-only books are skipped (scan) or refused (single import) with a warning.</summary>
public sealed class ImageOnlyImportTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _scanDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public ImageOnlyImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-image-only-{Guid.NewGuid():N}");
        _scanDir = Path.Combine(_tempDir, "scan");
        Directory.CreateDirectory(_scanDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _manager = new LibraryManager(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* Windows file locks */ }
    }

    private static GroupingPage Pic(int i) =>
        new($"pic{i}", $"text/pic{i:D3}.xhtml", "<div><img src=\"../images/p.jpg\" alt=\"\"/></div>");

    private static GroupingPage Prose(int i, int chars = 2500) =>
        new($"txt{i}", $"text/txt{i:D3}.xhtml", $"<p>{new string('あ', chars)}</p>");

    private string WriteEpub(string name, IEnumerable<GroupingPage> pages, string title)
    {
        string path = Path.Combine(_scanDir, name);
        File.WriteAllBytes(path, GroupingEpubFactory.BuildBytes(pages.ToList(), withToc: false, title: title));
        return path;
    }

    private string WriteManga(string name = "manga.epub") => WriteEpub(name, Enumerable.Range(0, 60).Select(Pic), "Some Manga");
    private string WriteNovel(string name = "novel.epub") => WriteEpub(name, Enumerable.Range(0, 12).Select(i => Prose(i)), "Some Novel");

    private int Count(string sql)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class SyncProgress : IProgress<ScanBatchProgress>
    {
        public List<ScanBatchProgress> Reports { get; } = [];
        public void Report(ScanBatchProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Scan_SkipsImageOnlyBook_ImportsTheNovel_AndWarns()
    {
        WriteNovel();
        WriteManga();

        var result = await _manager.ScanDirectoryAsync(_scanDir);

        Assert.Equal(2, result.TotalFilesDiscovered);
        Assert.Equal(1, result.ImportedNew);
        Assert.Equal(1, result.SkippedImageOnly);
        Assert.Equal(0, result.FailedFiles);                 // a skip is not a failure
        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings!);
        Assert.Contains("manga.epub", warning);
        Assert.Contains("not supported yet", warning);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM books;"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM books WHERE title = 'Some Novel';"));
    }

    [Fact]
    public async Task Scan_ImportsLightNovelWithManyIllustrations()
    {
        var pages = Enumerable.Range(0, 30).Select(i => Prose(i)).Concat(Enumerable.Range(0, 10).Select(Pic));
        WriteEpub("lightnovel.epub", pages, "Illustrated Novel");

        var result = await _manager.ScanDirectoryAsync(_scanDir);

        Assert.Equal(1, result.ImportedNew);
        Assert.Equal(0, result.SkippedImageOnly);
        Assert.Empty(result.Warnings!);
    }

    [Fact]
    public async Task Scan_ProgressReportsSkippedFiles_AndCountsThemAsScanned()
    {
        WriteNovel();
        WriteManga("a.epub");
        WriteManga("b.epub");
        var progress = new SyncProgress();

        await _manager.ScanDirectoryAsync(_scanDir, progress: progress);

        var last = progress.Reports[^1];
        Assert.Equal(2, last.SkippedFiles);
        Assert.Equal(3, last.ScannedFiles);
        Assert.Equal(3, last.TotalFiles);
    }

    [Fact]
    public async Task Scan_SkipsEveryTime_AndWritesNothingForTheSkippedBook()
    {
        WriteManga();

        var first = await _manager.ScanDirectoryAsync(_scanDir);
        var second = await _manager.ScanDirectoryAsync(_scanDir);

        Assert.Equal(1, first.SkippedImageOnly);
        Assert.Equal(1, second.SkippedImageOnly);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM books;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM book_chapters;"));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "covers")) && Directory.GetFiles(Path.Combine(_tempDir, "covers")).Length > 0);
    }

    [Fact]
    public async Task ImportBook_RejectsImageOnlyBook_WithAClearMessage_AndWritesNothing()
    {
        string path = WriteManga();

        var ex = await Assert.ThrowsAsync<UnsupportedBookException>(() => _manager.ImportBookAsync(path));

        Assert.Contains("manga.epub", ex.Message);
        Assert.Contains("60 of 60 pages are images", ex.Message);
        Assert.Contains("not supported yet", ex.Message);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM books;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM book_chapters;"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM books_fts;"));
    }

    [Fact]
    public async Task ImportBook_StillImportsTextBooks()
    {
        var detail = await _manager.ImportBookAsync(WriteNovel());

        Assert.Equal("Some Novel", detail.Summary.Title);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM books;"));
    }

    [Fact]
    public async Task Scan_LeavesAnAlreadyImportedImageOnlyBookCompletelyUntouched()
    {
        // An image-only book imported before this rule existed: same file hash, row + chapter already present.
        string path = WriteManga();
        var (bookId, sha) = await BookIdentity.ComputeFromFileAsync(path);
        Exec("""
            INSERT INTO books (book_id, epub_sha256, title, sort_title, folder_path, epub_file_name, chapter_count, added_at_utc, updated_at_utc)
            VALUES ($id, $sha, 'Legacy Manga', 'Legacy Manga', $dir, 'manga.epub', 1, '2020-01-01T00:00:00Z', '2020-01-01T00:00:00Z');
            """, ("$id", bookId), ("$sha", sha), ("$dir", _scanDir));
        Exec("""
            INSERT INTO book_chapters (book_id, chapter_index, chapter_id, title, spine_href)
            VALUES ($id, 0, 'chapter-01', 'Legacy chapter', 'text/pic000.xhtml');
            """, ("$id", bookId));

        var result = await _manager.ScanDirectoryAsync(_scanDir);

        Assert.Equal(1, result.SkippedImageOnly);
        Assert.Equal(0, result.DeduplicatedExisting);        // neither deduplicated nor updated
        Assert.Equal(0, result.ImportedNew);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM books WHERE updated_at_utc = '2020-01-01T00:00:00Z' AND title = 'Legacy Manga';"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM book_chapters WHERE title = 'Legacy chapter';"));
    }

    [Fact]
    public async Task Scan_ASkippedBookNeverBlocksTheRestOfTheBatch()
    {
        for (int i = 0; i < 5; i++) WriteManga($"manga{i}.epub");
        for (int i = 0; i < 5; i++) WriteEpub($"novel{i}.epub", Enumerable.Range(0, 8).Select(j => Prose(j, 1800 + i)), $"Novel {i}");

        var result = await _manager.ScanDirectoryAsync(_scanDir);

        Assert.Equal(5, result.ImportedNew);
        Assert.Equal(5, result.SkippedImageOnly);
        Assert.Equal(5, result.Warnings!.Count);
        Assert.Equal(0, result.FailedFiles);
    }

    private void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
