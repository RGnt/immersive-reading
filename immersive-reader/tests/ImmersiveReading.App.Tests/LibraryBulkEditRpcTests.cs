using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.App.Rpc.Modules;
using ImmersiveReading.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.App.Tests;

/// <summary>The JSON-RPC contract of the bulk edit dialog: method names, camelCase payloads and result shapes.</summary>
public class LibraryBulkEditRpcTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ir-bulk-rpc-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryManager _manager;
    private readonly Dictionary<string, RpcHandler> _handlers;

    public LibraryBulkEditRpcTests()
    {
        Directory.CreateDirectory(_tempDir);
        var db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        db.InitializeSchema();
        _manager = new LibraryManager(db);
        _handlers = new LibraryRpcModule(_manager).Handlers.ToDictionary(h => h.Method, h => h.Handler);
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
            Entry("OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title><dc:creator>作者</dc:creator><dc:language>ja</dc:language><dc:identifier id="id">urn:uuid:{Guid.NewGuid()}</dc:identifier></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>""");
            Entry("OEBPS/c.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><body><p>{Guid.NewGuid()}</p></body></html>""");
        }
        return (await _manager.ImportBookAsync(path)).Summary.BookId;
    }

    private async Task<JsonElement> Call(string method, object? payload = null)
    {
        var request = new RpcRequest(method, JsonSerializer.SerializeToElement(payload ?? new { }, RpcJson.Options));
        object? result = await _handlers[method](request, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result, RpcJson.Options);
    }

    [Fact]
    public async Task PreviewThenApply_WorkThroughRpc_WithTheShapeTheDialogExpects()
    {
        string a = await Import("a.epub", "Alpha 10");
        string b = await Import("b.epub", "Alpha 2");
        var payload = new { bookIds = new[] { a, b }, seriesName = "Alpha Saga", numberingMode = "title", autoNumberStart = 1, autoNumberStep = 1, authors = "Someone" };

        var preview = await Call("library.previewBulkUpdate", payload);
        Assert.Equal(2, preview.GetProperty("bookCount").GetInt32());
        Assert.Equal(2, preview.GetProperty("seriesChanges").GetInt32());
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("Alpha 2", rows.Single(r => r.GetProperty("newIndex").GetDouble() == 1).GetProperty("title").GetString());
        Assert.True(rows[0].TryGetProperty("currentSeries", out _));

        var applied = await Call("library.bulkUpdateBooks", payload);
        Assert.Equal(2, applied.GetProperty("updatedCount").GetInt32());
        var book = _manager.GetBookSummary(b)!;
        Assert.Equal(("Alpha Saga", 1.0, "Someone"), (book.SeriesName, book.SeriesIndex, book.Authors));
    }

    [Fact]
    public async Task ListBookIds_AndSeriesNames_WorkThroughRpc()
    {
        string a = await Import("a.epub", "Alpha");
        string b = await Import("b.epub", "Beta");
        await Call("library.bulkUpdateBooks", new { bookIds = new[] { a }, seriesName = "Solo" });

        var ids = await Call("library.listBookIds", new { search = (string?)null, limit = 1 });
        Assert.Equal(2, ids.GetProperty("total").GetInt32());
        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), ids.GetProperty("bookIds").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));

        var names = await Call("library.listSeriesNames");
        Assert.Equal("Solo", names[0].GetProperty("name").GetString());
        Assert.Equal(1, names[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheTagFilterTheSidebarSends_NarrowsBothTheListAndTheSelectAllLookup()
    {
        string a = await Import("a.epub", "Alpha");
        await Import("b.epub", "Beta");
        var tag = _manager.SaveTag(null, "pick");
        await Call("library.bulkUpdateBooks", new { bookIds = new[] { a }, addTagIds = new[] { tag.TagId } });

        var list = await Call("library.listBooks", new { tagIds = new[] { tag.TagId }, limit = 100 });
        var ids = await Call("library.listBookIds", new { tagIds = new[] { tag.TagId } });

        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, ids.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AnUnknownNumberingMode_IsAnErrorNotASilentNoOp()
    {
        string a = await Import("a.epub", "Alpha");

        await Assert.ThrowsAsync<ArgumentException>(() => Call("library.bulkUpdateBooks", new { bookIds = new[] { a }, numberingMode = "alphabetical" }));
    }
}
