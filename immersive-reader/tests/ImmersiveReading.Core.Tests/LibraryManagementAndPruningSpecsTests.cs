using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.Core.Tests;

public sealed class LibraryManagementAndPruningSpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public LibraryManagementAndPruningSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-management-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "library.db");
        string coversDir = Path.Combine(_tempDir, "covers");
        _db = new LibraryDatabase(dbPath, coversDir);
        _db.InitializeSchema();
        _manager = new LibraryManager(_db, Path.Combine(_tempDir, "output"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort on Windows
        }
    }

    [Fact]
    public void PruneEmptyFolders_RemovesEmptyFoldersBottomUp()
    {
        // 1. Create a hierarchy: Parent -> Child -> Leaf
        var parent = _manager.SaveFolder(null, null, "Parent Collection", "collection");
        var child = _manager.SaveFolder(null, parent.FolderId, "Child Category", "category");
        var leaf = _manager.SaveFolder(null, child.FolderId, "Empty Leaf Series", "series");

        // 2. Also create an active folder with a book
        var active = _manager.SaveFolder(null, null, "Active Collection", "collection");
        InsertTestBook("book-active-1", active.FolderId, "Active Book");

        // 3. Prune empty folders
        var pruned = _manager.PruneEmptyFolders();

        Assert.Equal(3, pruned.Count);
        Assert.Contains("Empty Leaf Series", pruned);
        Assert.Contains("Child Category", pruned);
        Assert.Contains("Parent Collection", pruned);

        // 4. Verify Active Collection is NOT pruned
        var tree = _manager.GetFolderTree();
        Assert.Single(tree);
        Assert.Equal("Active Collection", tree[0].Name);
    }

    [Fact]
    public void BulkDeleteBooks_DeletesBooksAndFtsEntries()
    {
        InsertTestBook("book-del-1", null, "Book To Delete One");
        InsertTestBook("book-del-2", null, "Book To Delete Two");
        InsertTestBook("book-keep-3", null, "Book To Keep");

        var initialList = _manager.ListBooks(new BookListQuery());
        Assert.Equal(3, initialList.TotalCount);

        int deleted = _manager.BulkDeleteBooks(new[] { "book-del-1", "book-del-2" });
        Assert.Equal(2, deleted);

        var afterList = _manager.ListBooks(new BookListQuery());
        Assert.Equal(1, afterList.TotalCount);
        Assert.Equal("book-keep-3", afterList.Items[0].BookId);
    }

    [Fact]
    public void MoveFolder_ReparentsFolderAndRejectsCycles()
    {
        var rootA = _manager.SaveFolder(null, null, "Root A", "collection");
        var rootB = _manager.SaveFolder(null, null, "Root B", "collection");
        var child = _manager.SaveFolder(null, rootA.FolderId, "Child of A", "folder");

        // Move child under rootB
        bool moved = _manager.MoveFolder(child.FolderId, rootB.FolderId);
        Assert.True(moved);

        var tree = _manager.GetFolderTree();
        var bNode = tree.First(f => f.FolderId == rootB.FolderId);
        Assert.NotNull(bNode.Children);
        Assert.Single(bNode.Children);
        Assert.Equal(child.FolderId, bNode.Children[0].FolderId);

        // Attempt cyclic parenting: move rootB under child (must fail!)
        bool cyclicMove = _manager.MoveFolder(rootB.FolderId, child.FolderId);
        Assert.False(cyclicMove);

        // Attempt self-parenting: move rootB under rootB (must fail!)
        bool selfMove = _manager.MoveFolder(rootB.FolderId, rootB.FolderId);
        Assert.False(selfMove);
    }

    [Fact]
    public void ReorderBooks_UpdatesCustomSortOrder()
    {
        InsertTestBook("b-1", null, "First Book");
        InsertTestBook("b-2", null, "Second Book");
        InsertTestBook("b-3", null, "Third Book");

        // Reorder in reverse: b-3, b-1, b-2
        _manager.ReorderBooks(new[] { "b-3", "b-1", "b-2" });

        var list = _manager.ListBooks(new BookListQuery(Sort: "custom"));
        Assert.Equal("b-3", list.Items[0].BookId);
        Assert.Equal("b-1", list.Items[1].BookId);
        Assert.Equal("b-2", list.Items[2].BookId);
    }

    private void InsertTestBook(string bookId, string? folderId, string title)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        string now = DateTime.UtcNow.ToString("O");
        cmd.CommandText = """
            INSERT INTO books (
                book_id, epub_sha256, folder_id, title, sort_title,
                series_name, series_index, custom_sort_order, authors, author_sort,
                isbn, publisher, imprint, published_date, language, description,
                is_metadata_edited, folder_path, epub_file_name, file_size_bytes,
                cover_cache_path, chapter_count, total_char_count, stats_computed,
                writing_mode, added_at_utc, updated_at_utc, has_study_pack
            ) VALUES (
                $id, $sha, $fld, $title, $title,
                '', NULL, 0, 'Author', 'Author',
                '', '', '', '2025-01-01', 'ja', '',
                0, 'C:\\Books', 'book.epub', 1000,
                NULL, 1, 100, 1,
                'vertical-rl', $now, $now, 0
            );
            INSERT INTO books_fts (book_id, title, sort_title, series_name, authors, publisher, isbn)
            VALUES ($id, $title, $title, '', 'Author', '', '');
            """;
        cmd.Parameters.AddWithValue("$id", bookId);
        cmd.Parameters.AddWithValue("$sha", $"sha256-{bookId}");
        cmd.Parameters.AddWithValue("$fld", (object?)folderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }
}
