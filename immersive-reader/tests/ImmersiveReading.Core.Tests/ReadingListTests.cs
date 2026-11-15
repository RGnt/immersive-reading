using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;
using ImmersiveReading.Library.Books;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Core.Tests;

/// <summary>Favourites and the reading list (currently reading / planning to read / completed) per book and per series, and the bookmark line.</summary>
public sealed class ReadingListTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ir-lists-tests-{Guid.NewGuid():N}");
    private readonly LibraryManager _manager;

    public ReadingListTests()
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

    private async Task<string> Import(string file, string title, string? series = null)
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
        string id = (await _manager.ImportBookAsync(path)).Summary.BookId;
        // the importer guesses series from titles: set what the test needs explicitly
        _manager.BulkUpdateBooks(new BulkBookUpdatePatch([id], SeriesName: series ?? "", ClearSeriesIndex: series is null));
        return id;
    }

    private BookSummary Book(string id) => _manager.GetBookSummary(id)!;
    private string[] Ids(BookListQuery q) => _manager.ListBookIds(q).OrderBy(x => x).ToArray();

    [Fact]
    public async Task ABooksOwnFlagsAreStoredAndShown_AbsentOnesAreLeftAlone_AndAnEmptyStatusClears()
    {
        string a = await Import("a.epub", "Alpha");

        Assert.False(Book(a).IsFavorite);
        Assert.Equal("", Book(a).ReadingStatus);

        _manager.SetBookFlags([a], favorite: true, readingStatus: "planned");
        Assert.Equal((true, "planned"), (Book(a).IsFavorite, Book(a).ReadingStatus));

        _manager.SetBookFlags([a], favorite: null, readingStatus: "reading");          // favourite untouched
        Assert.Equal((true, "reading"), (Book(a).IsFavorite, Book(a).ReadingStatus));

        _manager.SetBookFlags([a], favorite: false, readingStatus: null);              // status untouched
        Assert.Equal((false, "reading"), (Book(a).IsFavorite, Book(a).ReadingStatus));

        _manager.SetBookFlags([a], favorite: null, readingStatus: "");                 // cleared
        Assert.Equal("", Book(a).ReadingStatus);
        Assert.Throws<ArgumentException>(() => _manager.SetBookFlags([a], null, "finished"));
        Assert.Equal("", Book(a).ReadingStatus);
    }

    [Fact]
    public async Task TheListsFilterByTheEffectiveFlags_ABookInheritsItsSeries_ButItsOwnStatusWins()
    {
        string v1 = await Import("v1.epub", "Saga 1", "The Saga");
        string v2 = await Import("v2.epub", "Saga 2", "The Saga");
        string v3 = await Import("v3.epub", "Saga 3", "The Saga");
        string solo = await Import("solo.epub", "Alone");

        _manager.SetSeriesFlags("The Saga", favorite: true, readingStatus: "reading");
        _manager.SetBookFlags([v3], null, "completed");                                // volume 3 is finished, the series is still being read
        _manager.SetBookFlags([solo], true, "planned");

        Assert.Equal(new[] { v1, v2 }.OrderBy(x => x), Ids(new BookListQuery(ReadingStatus: "reading")));
        Assert.Equal([v3], Ids(new BookListQuery(ReadingStatus: "completed")));
        Assert.Equal([solo], Ids(new BookListQuery(ReadingStatus: "planned")));
        Assert.Equal(new[] { v1, v2, v3, solo }.OrderBy(x => x), Ids(new BookListQuery(Favorite: true)));    // every volume of a favourite series
        Assert.Equal(new[] { v1, v2, v3, solo }.OrderBy(x => x), Ids(new BookListQuery(ReadingStatus: "any")));

        // the raw flags travel with the book so a screen can edit the book and the series separately
        Assert.Equal((false, "", true, "reading"), (Book(v1).IsFavorite, Book(v1).ReadingStatus, Book(v1).SeriesIsFavorite, Book(v1).SeriesReadingStatus));
        Assert.Equal("completed", Book(v3).ReadingStatus);

        var counts = _manager.GetReadingListCounts();
        Assert.Equal(new ReadingListCounts(Favorites: 4, Reading: 2, Planned: 1, Completed: 1), counts);
    }

    [Fact]
    public async Task ClearingTheSeriesStatusTakesTheInheritedBooksOffTheList_AndTheListFilterComposesWithOthers()
    {
        string v1 = await Import("v1.epub", "Saga 1", "The Saga");
        string other = await Import("o.epub", "Other", "Another");
        _manager.SetSeriesFlags("The Saga", null, "reading");
        _manager.SetSeriesFlags("Another", null, "reading");
        Assert.Equal(2, Ids(new BookListQuery(ReadingStatus: "reading")).Length);

        _manager.SetSeriesFlags("The Saga", null, "");

        Assert.Equal([other], Ids(new BookListQuery(ReadingStatus: "reading")));
        Assert.Equal([], Ids(new BookListQuery(ReadingStatus: "reading", SeriesName: "The Saga")));
        Assert.Equal("", Book(v1).SeriesReadingStatus);
        Assert.Throws<ArgumentException>(() => _manager.SetSeriesFlags("  ", true, null));
    }

    // ------------------------------------------------------------------ bookmark line
    [Theory]
    [InlineData(null, 0, null, "Vol1Ch1")]
    [InlineData(3.0, 11, "", "Vol3Ch12")]
    [InlineData(2.5, 0, "   ", "Vol2.5Ch1")]
    [InlineData(3.0, 11, "ミミは流木を集めて、テントのそばに積みました。", "[ch12] ミミは流木を集めて、テントのそばに積みました。")]
    [InlineData(1.0, 4, "two\n  lines   here", "[ch5] two lines here")]
    public void TheBookmarkLineIsVolumeAndChapter_OrTheSelectedText(double? volume, int chapterIndex, string? selected, string expected)
        => Assert.Equal(expected, BookmarkLabel.Make(volume, chapterIndex, selected));

    [Fact]
    public void ALongSelectionIsShortenedWithAnEllipsis()
    {
        string label = BookmarkLabel.Make(1, 0, new string('あ', 200));

        Assert.StartsWith("[ch1] ", label);
        Assert.EndsWith("…", label);
        Assert.Equal("[ch1] ".Length + BookmarkLabel.MaxTextChars + 1, label.Length);
    }
}
