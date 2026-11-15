using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;
using ImmersiveReading.Library.Duplicates;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Core.Tests;

/// <summary>Spec 21: resolving duplicates against a real library database with real (tiny) EPUB files.</summary>
public sealed class DuplicateServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ir-dup-tests-{Guid.NewGuid():N}");
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public DuplicateServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _manager = new LibraryManager(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private string Epub(string dir, string fileName, string title, string author, string body)
    {
        string path = Path.Combine(_tempDir, dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(text);
        }
        Entry("mimetype", "application/epub+zip");
        Entry("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);
        Entry("OEBPS/content.opf", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{System.Security.SecurityElement.Escape(title)}</dc:title>
                <dc:creator>{System.Security.SecurityElement.Escape(author)}</dc:creator>
                <dc:language>ja</dc:language>
                <dc:identifier id="id">urn:uuid:{Guid.NewGuid()}</dc:identifier>
              </metadata>
              <manifest><item id="ch1" href="text/ch01.xhtml" media-type="application/xhtml+xml"/></manifest>
              <spine><itemref idref="ch1"/></spine>
            </package>
            """);
        Entry("OEBPS/text/ch01.xhtml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{System.Security.SecurityElement.Escape(title)}</title></head><body><p>{body}</p></body></html>
            """);
        return path;
    }

    private async Task<(string Id, string Path)> Import(string dir, string file, string title, string author, string body = "本文です。これは短い本です。")
    {
        string path = Epub(dir, file, title, author, body);
        var detail = await _manager.ImportBookAsync(path);
        return (detail.Summary.BookId, path);
    }

    private int Count(string sql, params (string, object)[] args)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ------------------------------------------------------------------ finding
    [Fact]
    public async Task Find_GroupsEditionsFromRealImports_AndLeavesOtherVolumesAlone()
    {
        var a = await Import("a", "one.epub", "ソードアート・オンライン 1", "川原 礫", "一巻の本文です。");
        var b = await Import("b", "one-ebook.epub", "ソードアート・オンライン（1）【電子版】", "川原礫", "一巻の本文です！");
        var other = await Import("a", "two.epub", "ソードアート・オンライン 2", "川原礫", "二巻の本文です。");

        var result = _manager.FindDuplicates();

        var group = Assert.Single(result.Groups);
        Assert.Equal(3, result.TotalBooks);
        Assert.Equal(new[] { a.Id, b.Id }.Order(StringComparer.Ordinal), group.Members.Select(m => m.Book.BookId).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(group.Members, m => m.Book.BookId == other.Id);
    }

    [Fact]
    public async Task Find_RecommendsTheBookWithReadingProgress()
    {
        var a = await Import("a", "a.epub", "吾輩は猫である", "夏目漱石", "本文A。");
        var b = await Import("b", "b.epub", "吾輩は猫である", "夏目漱石", "本文B。");
        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO reading_progress(book_id, current_chapter_index, current_spine_href, overall_completion, updated_at_utc) VALUES ($id, 0, 'x', 42, 't')";
            cmd.Parameters.AddWithValue("$id", b.Id);
            cmd.ExecuteNonQuery();
        }

        var group = Assert.Single(_manager.FindDuplicates().Groups);
        Assert.Equal(b.Id, group.RecommendedKeepId);
        Assert.Contains(group.Members[0].KeepReasons, r => r.Contains("42%"));
    }

    // ------------------------------------------------------------------ resolving
    [Fact]
    public async Task Resolve_KeepsTheUsersDataAndRemovesOnlyTheLibraryEntry()
    {
        var keep = await Import("a", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("b", "dup.epub", "同じ本", "山田太郎", "本文B。");
        var tag = _manager.SaveTag(null, "favourite");
        _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(dup.Id, SeriesName: "同じシリーズ", SeriesIndex: 2, TagIds: [tag.TagId],
            CustomFields: [new CustomFieldValueInput("vocab_deck_csv", Path.Combine(_tempDir, "b", "deck.csv"))]));
        _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(keep.Id, ClearSeriesIndex: true));

        var result = _manager.ResolveDuplicates(keep.Id, [dup.Id]);

        Assert.Equal(1, result.Removed);
        Assert.Null(_manager.GetBookSummary(dup.Id));
        var kept = _manager.GetBookDetail(keep.Id)!;
        Assert.Contains(kept.Summary.Tags, t => t.Name == "favourite");
        Assert.Equal("同じシリーズ", kept.Summary.SeriesName);
        Assert.Equal(2, kept.Summary.SeriesIndex);
        Assert.EndsWith("deck.csv", kept.DirectBookCustomFields["vocab_deck_csv"]);
        Assert.True(File.Exists(dup.Path), "the EPUB file of a removed duplicate is never deleted");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM removed_books WHERE book_id = $id", ("$id", dup.Id)));
        Assert.Empty(_manager.FindDuplicates().Groups);
    }

    [Fact]
    public async Task Resolve_DoesNotOverwriteWhatTheKeptBookAlreadyHas()
    {
        var keep = await Import("a", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("b", "dup.epub", "同じ本", "山田太郎", "本文B。");
        _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(keep.Id, SeriesName: "私のシリーズ", Publisher: "私の出版社"));
        _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(dup.Id, SeriesName: "別シリーズ", Publisher: "別出版社", Isbn: "9784048931502"));

        _manager.ResolveDuplicates(keep.Id, [dup.Id]);

        var kept = _manager.GetBookSummary(keep.Id)!;
        Assert.Equal("私のシリーズ", kept.SeriesName);
        Assert.Equal("私の出版社", kept.Publisher);
        Assert.Equal("9784048931502", kept.Isbn);              // only the missing field was filled
    }

    [Fact]
    public async Task Resolve_WithoutMerge_DropsTheDuplicatesDataOnly()
    {
        var keep = await Import("a", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("b", "dup.epub", "同じ本", "山田太郎", "本文B。");
        var tag = _manager.SaveTag(null, "favourite");
        _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(dup.Id, TagIds: [tag.TagId]));

        _manager.ResolveDuplicates(keep.Id, [dup.Id], mergeData: false);

        Assert.Empty(_manager.GetBookSummary(keep.Id)!.Tags);
    }

    [Fact]
    public async Task Resolve_InvalidInput_ChangesNothing()
    {
        var keep = await Import("a", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("b", "dup.epub", "同じ本", "山田太郎", "本文B。");

        Assert.Throws<ArgumentException>(() => _manager.ResolveDuplicates(keep.Id, [keep.Id]));
        Assert.Throws<ArgumentException>(() => _manager.ResolveDuplicates(keep.Id, []));
        Assert.Throws<KeyNotFoundException>(() => _manager.ResolveDuplicates(keep.Id, [dup.Id, "no-such-book"]));
        Assert.Throws<KeyNotFoundException>(() => _manager.ResolveDuplicates("no-such-book", [dup.Id]));

        Assert.NotNull(_manager.GetBookSummary(dup.Id));       // the valid id in the failed call was not removed
        Assert.Equal(0, Count("SELECT COUNT(*) FROM removed_books"));
    }

    // ------------------------------------------------------------------ mass clear
    [Fact]
    public async Task ResolveMany_ClearsEveryGroup_KeepingTheRecommendedBook()
    {
        var a1 = await Import("a", "neko.epub", "吾輩は猫である", "夏目漱石", "本文A1。");
        var a2 = await Import("b", "neko.epub", "吾輩は猫である", "夏目漱石", "本文A2。");
        var b1 = await Import("a", "botchan.epub", "坊っちゃん", "夏目漱石", "本文B1。");
        var b2 = await Import("b", "botchan.epub", "坊っちゃん", "夏目漱石", "本文B2。");
        var solo = await Import("a", "kokoro.epub", "こころ", "夏目漱石", "本文C。");

        var groups = _manager.FindDuplicates().Groups;
        Assert.Equal(2, groups.Count);
        var requests = groups.Select(g => new ResolveGroupRequest(g.RecommendedKeepId, g.Members.Select(m => m.Book.BookId).Where(id => id != g.RecommendedKeepId).ToList())).ToList();

        var result = _manager.ResolveDuplicatesBatch(requests);

        Assert.Equal(2, result.GroupsResolved);
        Assert.Equal(2, result.Removed);
        Assert.Empty(result.Failed);
        Assert.Empty(_manager.FindDuplicates().Groups);
        Assert.NotNull(_manager.GetBookSummary(solo.Id));
        Assert.Equal(2, _manager.ListRemovedBooks().Count);
        Assert.All(new[] { a1.Path, a2.Path, b1.Path, b2.Path }, f => Assert.True(File.Exists(f)));
        foreach (var r in requests)
        {
            Assert.NotNull(_manager.GetBookSummary(r.KeepBookId));
            Assert.All(r.RemoveBookIds, id => Assert.Null(_manager.GetBookSummary(id)));
        }
    }

    [Fact]
    public async Task ResolveMany_AStaleGroupIsReported_AndTheOthersStillGoThrough()
    {
        var a1 = await Import("a", "neko.epub", "吾輩は猫である", "夏目漱石", "本文A1。");
        var a2 = await Import("b", "neko.epub", "吾輩は猫である", "夏目漱石", "本文A2。");
        var b1 = await Import("a", "botchan.epub", "坊っちゃん", "夏目漱石", "本文B1。");
        var b2 = await Import("b", "botchan.epub", "坊っちゃん", "夏目漱石", "本文B2。");
        _manager.DeleteBook(b2.Id);                        // the second group went stale since the search

        var result = _manager.ResolveDuplicatesBatch(
        [
            new ResolveGroupRequest(b1.Id, [b2.Id]),
            new ResolveGroupRequest(a1.Id, [a2.Id]),
        ]);

        Assert.Equal(1, result.GroupsResolved);
        Assert.Equal([a1.Id], result.ResolvedKeepBookIds);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(b1.Id, failure.KeepBookId);
        Assert.NotNull(_manager.GetBookSummary(b1.Id));
        Assert.Null(_manager.GetBookSummary(a2.Id));
    }

    [Fact]
    public async Task ResolveMany_RejectsAGroupThatReusesABookOfAnEarlierGroup()
    {
        var a = await Import("a", "a.epub", "吾輩は猫である", "夏目漱石", "本文A。");
        var b = await Import("b", "b.epub", "吾輩は猫である", "夏目漱石", "本文B。");
        var c = await Import("c", "c.epub", "吾輩は猫である", "夏目漱石", "本文C。");

        var result = _manager.ResolveDuplicatesBatch([new ResolveGroupRequest(a.Id, [b.Id]), new ResolveGroupRequest(c.Id, [b.Id])]);

        Assert.Equal(1, result.GroupsResolved);
        Assert.Single(result.Failed);
        Assert.NotNull(_manager.GetBookSummary(c.Id));
    }

    // ------------------------------------------------------------------ decisions persist
    [Fact]
    public async Task Dismiss_IsRememberedAndForgottenWithTheBook()
    {
        var a = await Import("a", "a.epub", "同じ本", "山田太郎", "本文A。");
        var b = await Import("b", "b.epub", "同じ本", "山田太郎", "本文B。");
        Assert.Single(_manager.FindDuplicates().Groups);

        Assert.Equal(1, _manager.DismissDuplicates([a.Id, b.Id]));
        Assert.Empty(_manager.FindDuplicates().Groups);

        _manager.DeleteBook(b.Id);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM duplicate_dismissals"));
        Assert.Throws<ArgumentException>(() => _manager.DismissDuplicates([a.Id]));
    }

    // ------------------------------------------------------------------ re-scan
    [Fact]
    public async Task Scan_SkipsRemovedDuplicates_UntilTheUserAllowsThemAgain()
    {
        var keep = await Import("lib", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("lib", "dup.epub", "同じ本", "山田太郎", "本文B。");
        _manager.ResolveDuplicates(keep.Id, [dup.Id]);
        string root = Path.Combine(_tempDir, "lib");

        var skipped = await _manager.ScanDirectoryAsync(root, recursive: true);
        Assert.Equal(1, skipped.SkippedRemovedDuplicates);
        Assert.Contains(skipped.Warnings!, w => w.Contains("dup.epub"));
        Assert.Null(_manager.GetBookSummary(dup.Id));

        Assert.True(_manager.ForgetRemovedBook(dup.Id));
        var again = await _manager.ScanDirectoryAsync(root, recursive: true);
        Assert.Equal(0, again.SkippedRemovedDuplicates);
        Assert.NotNull(_manager.GetBookSummary(dup.Id));
    }

    [Fact]
    public async Task ExplicitImport_OfARemovedFile_ClearsTheRemovalRecord()
    {
        var keep = await Import("lib", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("lib", "dup.epub", "同じ本", "山田太郎", "本文B。");
        _manager.ResolveDuplicates(keep.Id, [dup.Id]);
        Assert.Single(_manager.ListRemovedBooks());

        await _manager.ImportBookAsync(dup.Path);

        Assert.Empty(_manager.ListRemovedBooks());
        Assert.NotNull(_manager.GetBookSummary(dup.Id));
    }

    [Fact]
    public async Task ListRemoved_DescribesWhatWasRemoved()
    {
        var keep = await Import("lib", "keep.epub", "同じ本", "山田太郎", "本文A。");
        var dup = await Import("lib", "dup.epub", "同じ本", "山田太郎", "本文B。");
        _manager.ResolveDuplicates(keep.Id, [dup.Id]);

        var removed = Assert.Single(_manager.ListRemovedBooks());
        Assert.Equal(dup.Id, removed.BookId);
        Assert.Equal(keep.Id, removed.KeptBookId);
        Assert.Equal("dup.epub", removed.EpubFileName);
        Assert.Equal("同じ本", removed.Title);
    }
}
