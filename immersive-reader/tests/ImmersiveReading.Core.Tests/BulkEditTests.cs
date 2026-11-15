using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;
using ImmersiveReading.Library.Books;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Core.Tests;

/// <summary>Library management: bulk metadata editing (series, volume numbers, authors, tags ...) for many selected books at once.</summary>
public sealed class BulkEditTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ir-bulk-tests-{Guid.NewGuid():N}");
    private readonly LibraryManager _manager;

    public BulkEditTests()
    {
        Directory.CreateDirectory(_tempDir);
        var db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        db.InitializeSchema();
        _manager = new LibraryManager(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Import(string file, string title)
    {
        string path = Path.Combine(_tempDir, "in", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Entry(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            Entry("mimetype", "application/epub+zip");
            Entry("META-INF/container.xml", """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""");
            Entry("OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{System.Security.SecurityElement.Escape(title)}</dc:title><dc:creator>作者</dc:creator><dc:language>ja</dc:language><dc:identifier id="id">urn:uuid:{Guid.NewGuid()}</dc:identifier></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>""");
            Entry("OEBPS/c.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><body><p>{Guid.NewGuid()}</p></body></html>""");
        }
        return (await _manager.ImportBookAsync(path)).Summary.BookId;
    }

    private BookSummary Book(string id) => _manager.GetBookSummary(id)!;

    // ------------------------------------------------------------------ the numbering rules
    [Fact]
    public void Natural_OrderTreatsDigitRunsAsNumbers_AlsoFullWidthOnes()
    {
        var titles = new[] { "Saga 10", "Saga 2", "Saga 1", "第１０巻", "第２巻" };
        var sorted = titles.Order(NaturalComparer.Instance).ToArray();

        Assert.Equal(["Saga 1", "Saga 2", "Saga 10"], sorted.Where(t => t.StartsWith("Saga")));
        Assert.Equal(["第２巻", "第１０巻"], sorted.Where(t => t.StartsWith("第")));
    }

    private static NumberingInput In(string id, string title, string file = "") => new(id, title, "", file);

    [Fact]
    public void Numbering_ModesAssignTheExpectedVolumes()
    {
        var books = new[] { In("c", "Saga 10", "z.epub"), In("a", "Saga 2", "b.epub"), In("b", "Saga 1", "a.epub") };

        Assert.Equal(new() { ["c"] = 1, ["a"] = 2, ["b"] = 3 }, BulkNumbering.Compute(books, BulkNumbering.Selection, 1, 1));
        Assert.Equal(new() { ["b"] = 1, ["a"] = 2, ["c"] = 3 }, BulkNumbering.Compute(books, BulkNumbering.Title, 1, 1));
        Assert.Equal(new() { ["b"] = 5, ["a"] = 5.5, ["c"] = 6 }, BulkNumbering.Compute(books, BulkNumbering.FileName, 5, 0.5));
        Assert.Empty(BulkNumbering.Compute(books, null, 1, 1));
    }

    [Fact]
    public void Numbering_FromTitle_UsesTheVolumeWrittenInTheTitle_AndSkipsBooksWithoutOne()
    {
        var books = new[] { In("a", "賢者の弟子 第3巻"), In("b", "Title 12"), In("c", "No number at all"), In("d", "名前 III") };

        var numbers = BulkNumbering.Compute(books, BulkNumbering.FromTitle, 1, 1);

        Assert.Equal(3, numbers["a"]);
        Assert.Equal(12, numbers["b"]);
        Assert.Equal(3, numbers["d"]);
        Assert.False(numbers.ContainsKey("c"));
    }

    [Fact]
    public void Numbering_AnUnknownModeIsRefused() =>
        Assert.Throws<ArgumentException>(() => BulkNumbering.EffectiveMode("alphabetical", false));

    // ------------------------------------------------------------------ the edit
    [Fact]
    public async Task OneEdit_PutsManyBooksIntoASeries_NumberedByTitleNoMatterHowTheyWereSelected()
    {
        string v10 = await Import("a.epub", "Saga 10");
        string v2 = await Import("b.epub", "Saga 2");
        string v1 = await Import("c.epub", "Saga 1");

        int count = _manager.BulkUpdateBooks(new BulkBookUpdatePatch([v10, v2, v1], SeriesName: "  The Saga  ", NumberingMode: BulkNumbering.Title, Authors: "A. Writer"));

        Assert.Equal(3, count);
        Assert.Equal(1, Book(v1).SeriesIndex);
        Assert.Equal(2, Book(v2).SeriesIndex);
        Assert.Equal(3, Book(v10).SeriesIndex);
        Assert.All(new[] { v1, v2, v10 }, id =>
        {
            Assert.Equal("The Saga", Book(id).SeriesName);          // trimmed
            Assert.Equal("A. Writer", Book(id).Authors);
            Assert.True(Book(id).IsMetadataEdited);
        });
    }

    [Fact]
    public async Task Edit_WithoutNumbering_LeavesTheVolumeNumbersAlone()
    {
        string a = await Import("a.epub", "Alpha");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a], SeriesName: "One", NumberingMode: BulkNumbering.Selection, AutoNumberStart: 7));
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a], SeriesName: "Two"));

        Assert.Equal(("Two", 7.0), (Book(a).SeriesName, Book(a).SeriesIndex));
    }

    [Fact]
    public async Task Edit_FromTitle_KeepsTheNumberOfBooksWhoseTitleHasNone()
    {
        string a = await Import("a.epub", "Hero 4");
        string b = await Import("b.epub", "Lonely book");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([b], SeriesName: "Old", NumberingMode: BulkNumbering.Selection, AutoNumberStart: 9));

        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], SeriesName: "Hero", NumberingMode: BulkNumbering.FromTitle));

        Assert.Equal(4, Book(a).SeriesIndex);
        Assert.Equal(9, Book(b).SeriesIndex);
        Assert.Equal("Hero", Book(b).SeriesName);
    }

    [Fact]
    public async Task Edit_AnEmptySeriesNameRemovesTheBooksFromTheirSeries_NullLeavesItAlone_AndTheIndexCanBeCleared()
    {
        string a = await Import("a.epub", "Alpha");
        string b = await Import("b.epub", "Beta");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], SeriesName: "S", NumberingMode: BulkNumbering.Selection));

        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], Publisher: "P"));                  // series untouched
        Assert.Equal(("S", "P"), (Book(a).SeriesName, Book(a).Publisher));

        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a], SeriesName: "", ClearSeriesIndex: true));
        Assert.Equal(("", (double?)null), (Book(a).SeriesName, Book(a).SeriesIndex));
        Assert.Equal(("S", (double?)2), (Book(b).SeriesName, Book(b).SeriesIndex));
    }

    [Fact]
    public async Task Edit_CountsOnlyExistingBooks_AndEachSelectedBookOnce()
    {
        string a = await Import("a.epub", "Alpha");

        int count = _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, a, "book-does-not-exist"], SeriesName: "S", NumberingMode: BulkNumbering.Selection));

        Assert.Equal(1, count);
        Assert.Equal(1, Book(a).SeriesIndex);                       // not 2 for the repeated id
    }

    [Fact]
    public async Task Edit_AnUnknownNumberingModeChangesNothing()
    {
        string a = await Import("a.epub", "Alpha");

        Assert.Throws<ArgumentException>(() => _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a], SeriesName: "S", NumberingMode: "alphabetical")));

        Assert.Equal("", Book(a).SeriesName);
    }

    [Fact]
    public async Task Edit_AddsAndRemovesTagsOnAllSelectedBooks()
    {
        string a = await Import("a.epub", "Alpha");
        string b = await Import("b.epub", "Beta");
        var keep = _manager.SaveTag(null, "keep");
        var drop = _manager.SaveTag(null, "drop");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], AddTagIds: [keep.TagId, drop.TagId]));

        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], AddTagIds: [keep.TagId], RemoveTagIds: [drop.TagId]));    // adding twice is harmless

        Assert.All(new[] { a, b }, id => Assert.Equal(["keep"], Book(id).Tags.Select(t => t.Name)));
    }

    // ------------------------------------------------------------------ preview
    [Fact]
    public async Task Preview_ShowsExactlyWhatTheEditWritesAndWritesNothing()
    {
        string v10 = await Import("a.epub", "Saga 10");
        string v2 = await Import("b.epub", "Saga 2");
        string other = await Import("c.epub", "Other");
        // the importer already guessed series "Saga" and volumes 10 and 2 from the titles; "Other" has neither
        var patch = new BulkBookUpdatePatch([v10, v2, other], SeriesName: "The Saga", NumberingMode: BulkNumbering.Title);

        var preview = _manager.PreviewBulkUpdate(patch);

        // title order is Other(1), Saga 2(2), Saga 10(3): Saga 2 keeps its number, the other two change
        Assert.Equal((3, 3, 2, 0), (preview.BookCount, preview.SeriesChanges, preview.IndexChanges, preview.WithoutNumber));
        Assert.Equal("Saga", Book(v10).SeriesName);                                // nothing was written (still the importer's guess)
        _manager.BulkUpdateBooks(patch);
        Assert.All(preview.Rows, row =>
        {
            Assert.Equal(row.NewSeries, Book(row.BookId).SeriesName);
            Assert.Equal(row.NewIndex, Book(row.BookId).SeriesIndex);
        });
        Assert.Equal(["Other", "Saga 2", "Saga 10"], preview.Rows.OrderBy(r => r.NewIndex).Select(r => r.Title));
    }

    [Fact]
    public async Task Preview_FromTitleCountsBooksWithoutAFoundNumber_AndSamplesOnlyTheFirstRows()
    {
        string a = await Import("a.epub", "Hero 1");
        string b = await Import("b.epub", "Nameless");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a], NumberingMode: BulkNumbering.Selection, AutoNumberStart: 9));      // a wrong number to fix

        var preview = _manager.PreviewBulkUpdate(new BulkBookUpdatePatch([a, b], SeriesName: "Hero", NumberingMode: BulkNumbering.FromTitle));

        Assert.Equal((2, 1), (preview.BookCount, preview.WithoutNumber));
        Assert.Equal(1, preview.IndexChanges);                                      // a: 9 -> 1; b has no number in its title and keeps none
        var sampled = _manager.PreviewBulkUpdate(new BulkBookUpdatePatch([a, b]), maxRows: 1);
        Assert.Equal((2, 1), (sampled.BookCount, sampled.Rows.Count));
    }

    // ------------------------------------------------------------------ selecting
    [Fact]
    public async Task ListBookIds_ReturnsEveryMatchOfTheFilter_NotJustOnePage()
    {
        string a = await Import("a.epub", "Alpha");
        string b = await Import("b.epub", "Beta");
        string c = await Import("c.epub", "Gamma");
        var tag = _manager.SaveTag(null, "pick");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], AddTagIds: [tag.TagId]));

        var all = _manager.ListBookIds(new BookListQuery(Limit: 1));
        var tagged = _manager.ListBookIds(new BookListQuery(TagIds: [tag.TagId], Limit: 1));

        Assert.Equal(new[] { a, b, c }.Order(StringComparer.Ordinal), all.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), tagged.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListSeriesNames_CountsBooksPerSeries_IgnoringBooksWithoutOne()
    {
        string a = await Import("a.epub", "Alpha");
        string b = await Import("b.epub", "Beta");
        await Import("c.epub", "Gamma");
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([a, b], SeriesName: "Saga"));

        var names = _manager.ListSeriesNames();

        Assert.Equal([new SeriesNameCount("Saga", 2)], names);
    }
}
