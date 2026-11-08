using System.Text.Json;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Tests;

/// <summary>
/// Characterization tests for the JSON-RPC contract exposed by <see cref="LibraryRpcDispatcher"/>.
/// They pin the envelope and the result key names that wwwroot/app.js depends on,
/// so refactoring the dispatcher cannot silently change the wire format.
/// </summary>
public sealed class RpcContractTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryRpcDispatcher _dispatcher;
    private readonly List<string> _uiMessages = [];

    public RpcContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-app-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        var library = new LibraryManager(_db);
        var config = new ConfigurationManager(_db);
        _dispatcher = new LibraryRpcDispatcher(
            library,
            _db,
            sendMessageToUi: _uiMessages.Add,
            configManager: config);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup locks on Windows
        }
    }

    private async Task<JsonElement> CallAsync(string method, string paramsJson = "{}", string id = "t-1")
    {
        string request = $$"""{"id":"{{id}}","method":"{{method}}","params":{{paramsJson}}}""";
        string response = await _dispatcher.DispatchJsonAsync(request);
        return JsonDocument.Parse(response).RootElement.Clone();
    }

    private async Task<JsonElement> CallOkAsync(string method, string paramsJson = "{}")
    {
        var envelope = await CallAsync(method, paramsJson);
        Assert.True(
            !envelope.TryGetProperty("error", out _),
            $"{method} returned error: {envelope}");
        return envelope.GetProperty("result");
    }

    private static HashSet<string> Keys(JsonElement obj) =>
        obj.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public async Task Envelope_EchoesRequestId_AndOmitsError_OnSuccess()
    {
        var envelope = await CallAsync("library.listTags", id: "abc-42");

        Assert.Equal("abc-42", envelope.GetProperty("id").GetString());
        Assert.False(envelope.TryGetProperty("error", out _), "null error is omitted from the wire format");
        Assert.Equal(JsonValueKind.Array, envelope.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task Envelope_NumericId_IsEchoedAsRawText()
    {
        string response = await _dispatcher.DispatchJsonAsync("""{"id":7,"method":"library.listTags","params":{}}""");
        var envelope = JsonDocument.Parse(response).RootElement;

        Assert.Equal("7", envelope.GetProperty("id").GetString());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsNotSupportedException_WithStableMessage()
    {
        var envelope = await CallAsync("does.notExist");

        Assert.False(envelope.TryGetProperty("result", out _), "null result is omitted from the wire format");
        var error = envelope.GetProperty("error");
        Assert.Equal("NotSupportedException", error.GetProperty("type").GetString());
        Assert.Equal("Unknown RPC method: 'does.notExist'.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MalformedJson_ReturnsErrorEnvelope_()
    {
        string response = await _dispatcher.DispatchJsonAsync("{not json");
        var envelope = JsonDocument.Parse(response).RootElement;

        Assert.False(envelope.TryGetProperty("result", out _), "null result is omitted from the wire format");
        Assert.True(envelope.GetProperty("error").TryGetProperty("message", out _));
    }

    [Fact]
    public async Task MissingRequiredParam_ReturnsErrorEnvelope()
    {
        var envelope = await CallAsync("library.getBookDetail", "{}");

        Assert.True(envelope.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetBootstrapState_ReturnsExpectedKeys()
    {
        var result = await CallOkAsync("library.getBootstrapState");

        Assert.Equal(
            new[] { "config", "customFields", "defaultScanDir", "folders", "initialPage", "tags" },
            Keys(result).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("folders").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("customFields").ValueKind);
        Assert.Equal(JsonValueKind.Object, result.GetProperty("initialPage").ValueKind);
    }

    [Fact]
    public async Task GetBootstrapState_SeedsStarterFolders_OnEmptyLibrary()
    {
        var result = await CallOkAsync("library.getBootstrapState");

        var roots = result.GetProperty("folders").EnumerateArray().ToList();
        Assert.Contains(roots, f => f.GetProperty("folderId").GetString() == "fld-japanese");
    }

    [Fact]
    public async Task ListBooks_OnEmptyLibrary_ReturnsPagedResultShape()
    {
        var result = await CallOkAsync("library.listBooks");

        foreach (string key in new[] { "items", "totalCount", "offset", "limit", "queryElapsedMs" })
        {
            Assert.True(result.TryGetProperty(key, out _), $"missing key '{key}'");
        }
        Assert.Equal(0, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
    }

    [Fact]
    public async Task SaveFolder_ReturnsSavedAndTree_AndDeleteFolderRemovesIt()
    {
        var saved = await CallOkAsync(
            "library.saveFolder",
            """{"folderId":"fld-test","name":"Test Folder","folderKind":"category"}""");

        Assert.Equal(new[] { "saved", "tree" }, Keys(saved).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("fld-test", saved.GetProperty("saved").GetProperty("folderId").GetString());
        Assert.Equal("Test Folder", saved.GetProperty("saved").GetProperty("name").GetString());
        Assert.Equal("category", saved.GetProperty("saved").GetProperty("folderKind").GetString());

        var tree = await CallOkAsync("library.getFolderTree");
        Assert.Contains(tree.EnumerateArray(), f => f.GetProperty("folderId").GetString() == "fld-test");

        var deleted = await CallOkAsync("library.deleteFolder", """{"folderId":"fld-test"}""");
        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(
            deleted.GetProperty("tree").EnumerateArray(),
            f => f.GetProperty("folderId").GetString() == "fld-test");
    }

    [Fact]
    public async Task SaveTag_ReturnsTagAndTags_AndListTagsContainsIt()
    {
        var saved = await CallOkAsync("library.saveTag", """{"name":"favorites","colorHex":"#ff0000"}""");

        Assert.Equal(new[] { "tag", "tags" }, Keys(saved).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal("favorites", saved.GetProperty("tag").GetProperty("name").GetString());

        var tags = await CallOkAsync("library.listTags");
        Assert.Contains(tags.EnumerateArray(), t => t.GetProperty("name").GetString() == "favorites");

        string tagId = saved.GetProperty("tag").GetProperty("tagId").GetString()!;
        var deleted = await CallOkAsync("library.deleteTag", $$"""{"tagId":"{{tagId}}"}""");
        Assert.True(deleted.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(
            deleted.GetProperty("tags").EnumerateArray(),
            t => t.GetProperty("name").GetString() == "favorites");
    }

    [Fact]
    public async Task ListCustomFieldDefs_ReturnsSeededDefinitions()
    {
        var result = await CallOkAsync("library.listCustomFieldDefs");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0);
        Assert.True(result[0].TryGetProperty("fieldKey", out _));
    }

    [Fact]
    public async Task ConfigGetAll_ReturnsAllCategories()
    {
        var result = await CallOkAsync("config.getAll");

        Assert.Equal(
            new[] { "appearance", "dictionaries", "directories", "library", "llm", "reader" },
            Keys(result).OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConfigGetCategory_ReturnsCategory_AndRejectsUnknown()
    {
        var reader = await CallOkAsync("config.getCategory", """{"category":"reader"}""");
        Assert.True(reader.TryGetProperty("fontSizePx", out _));

        var bad = await CallAsync("config.getCategory", """{"category":"nope"}""");
        Assert.Equal("ArgumentException", bad.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConfigUpdate_FullConfig_PersistsAndEmitsChangedEvent()
    {
        var result = await CallOkAsync(
            "config.update",
            """{"config":{"reader":{"fontSizePx":26,"writingMode":"horizontal-tb"}}}""");

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(26, result.GetProperty("updatedConfig").GetProperty("reader").GetProperty("fontSizePx").GetInt32());

        var reread = await CallOkAsync("config.getAll");
        Assert.Equal(26, reread.GetProperty("reader").GetProperty("fontSizePx").GetInt32());

        string evt = Assert.Single(_uiMessages);
        using var evtDoc = JsonDocument.Parse(evt);
        Assert.Equal("config.changed", evtDoc.RootElement.GetProperty("event").GetString());
        Assert.Equal("all", evtDoc.RootElement.GetProperty("data").GetProperty("category").GetString());
    }

    [Fact]
    public async Task ConfigUpdate_WithoutConfigOrCategory_ReturnsArgumentException()
    {
        var envelope = await CallAsync("config.update", "{}");

        Assert.Equal("ArgumentException", envelope.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConfigResetDefaults_All_RestoresDefaults()
    {
        await CallOkAsync("config.update", """{"config":{"reader":{"fontSizePx":30}}}""");

        var result = await CallOkAsync("config.resetDefaults", """{"category":"all"}""");

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(22, result.GetProperty("updatedConfig").GetProperty("reader").GetProperty("fontSizePx").GetInt32());
    }

    [Fact]
    public async Task ExtensionsList_WithoutPluginHost_ReturnsEmptyArrays()
    {
        var result = await CallOkAsync("extensions.list");

        Assert.Equal(new[] { "plugins", "uiPanels" }, Keys(result).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(0, result.GetProperty("plugins").GetArrayLength());
        Assert.Equal(0, result.GetProperty("uiPanels").GetArrayLength());
    }
}
