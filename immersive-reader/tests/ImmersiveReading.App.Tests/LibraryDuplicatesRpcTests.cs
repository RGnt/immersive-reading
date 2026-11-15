using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.App.Rpc.Modules;
using ImmersiveReading.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.App.Tests;

/// <summary>Spec 21 section 6: the JSON-RPC contract the duplicates dialog relies on (method names, payloads, camelCase result shape).</summary>
public class LibraryDuplicatesRpcTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ir-dup-rpc-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryManager _manager;
    private readonly Dictionary<string, RpcHandler> _handlers;

    public LibraryDuplicatesRpcTests()
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

    private async Task<string> Import(string dir, string title, string body)
    {
        string path = Path.Combine(_tempDir, dir, "book.epub");
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
            Entry("OEBPS/content.opf", $"""<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title><dc:creator>夏目漱石</dc:creator><dc:language>ja</dc:language><dc:identifier id="id">urn:uuid:{Guid.NewGuid()}</dc:identifier></metadata><manifest><item id="c" href="c.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c"/></spine></package>""");
            Entry("OEBPS/c.xhtml", $"""<?xml version="1.0"?><html xmlns="http://www.w3.org/1999/xhtml"><body><p>{body}</p></body></html>""");
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
    public async Task FindResolveAndRemovedList_WorkThroughRpc_WithTheShapeTheUiExpects()
    {
        string a = await Import("a", "吾輩は猫である", "本文A。");
        string b = await Import("b", "吾輩は猫である【電子版】", "本文B。");

        var found = await Call("library.findDuplicates", new { minScore = 70 });
        Assert.Equal(2, found.GetProperty("totalBooks").GetInt32());
        var group = found.GetProperty("groups").EnumerateArray().Single();
        Assert.Equal("certain", group.GetProperty("confidence").GetString());
        Assert.False(string.IsNullOrEmpty(group.GetProperty("groupId").GetString()));
        string keep = group.GetProperty("recommendedKeepId").GetString()!;
        var member = group.GetProperty("members").EnumerateArray().First();
        Assert.True(member.GetProperty("book").TryGetProperty("title", out _));
        Assert.True(member.GetProperty("book").TryGetProperty("progressPercent", out _));
        Assert.True(member.TryGetProperty("keepReasons", out _));
        Assert.True(group.GetProperty("pairs")[0].TryGetProperty("reasons", out _));

        string remove = keep == a ? b : a;
        var resolved = await Call("library.resolveDuplicates", new { keepBookId = keep, removeBookIds = new[] { remove }, mergeData = true });
        Assert.Equal(1, resolved.GetProperty("removed").GetInt32());
        Assert.Null(_manager.GetBookSummary(remove));

        var removed = (await Call("library.listRemovedBooks")).EnumerateArray().Single();
        Assert.Equal(remove, removed.GetProperty("bookId").GetString());

        var forgotten = await Call("library.forgetRemovedBook", new { bookId = remove });
        Assert.True(forgotten.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Dismiss_ThroughRpc_HidesTheGroup()
    {
        string a = await Import("a", "吾輩は猫である", "本文A。");
        string b = await Import("b", "吾輩は猫である", "本文B。");

        var dismissed = await Call("library.dismissDuplicates", new { bookIds = new[] { a, b } });

        Assert.Equal(1, dismissed.GetProperty("pairs").GetInt32());
        Assert.Empty((await Call("library.findDuplicates")).GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task ResolveBatch_ThroughRpc_ClearsGroupsAndReportsFailures()
    {
        string a = await Import("a", "吾輩は猫である", "本文A。");
        string b = await Import("b", "吾輩は猫である", "本文B。");

        var result = await Call("library.resolveDuplicatesBatch", new
        {
            mergeData = true,
            groups = new object[]
            {
                new { keepBookId = a, removeBookIds = new[] { b } },
                new { keepBookId = "gone", removeBookIds = new[] { "also-gone" } }
            }
        });

        Assert.Equal(1, result.GetProperty("groupsResolved").GetInt32());
        Assert.Equal(1, result.GetProperty("removed").GetInt32());
        Assert.Equal(a, result.GetProperty("resolvedKeepBookIds")[0].GetString());
        Assert.Equal("gone", result.GetProperty("failed")[0].GetProperty("keepBookId").GetString());
        Assert.Null(_manager.GetBookSummary(b));
    }

    [Fact]
    public async Task ResolveBatch_WithoutGroups_FailsWithAClearError()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Call("library.resolveDuplicatesBatch", new { groups = Array.Empty<object>() }));
    }

    [Fact]
    public async Task Resolve_WithoutRequiredFields_FailsWithAClearError()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Call("library.resolveDuplicates", new { removeBookIds = new[] { "x" } }));
    }
}
