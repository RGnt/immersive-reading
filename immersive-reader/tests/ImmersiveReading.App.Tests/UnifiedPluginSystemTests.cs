using System.Text.Json;
using ImmersiveReading.App.Plugins;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ImmersiveReading.App.Tests;

public class UnifiedPluginSystemTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestRpcEventSink _events = new();

    public UnifiedPluginSystemTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ir-plugin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task PluginDatabaseContext_AppliesMigrationsAndTracksVersions()
    {
        var dbCtx = new PluginDatabaseContext(_tempDir);
        var migrations = new[]
        {
            new PluginMigration(1, "create_items", "CREATE TABLE items (id TEXT PRIMARY KEY, val TEXT);"),
            new PluginMigration(2, "add_indexes", "CREATE INDEX idx_items_val ON items(val);")
        };

        await dbCtx.MigrateAsync(migrations);

        await using var conn = (Microsoft.Data.Sqlite.SqliteConnection)dbCtx.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM __plugin_migrations;";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.Equal(2, count);

        cmd.CommandText = "INSERT INTO items (id, val) VALUES ('1', 'test');";
        await cmd.ExecuteNonQueryAsync();

        cmd.CommandText = "SELECT val FROM items WHERE id = '1';";
        var val = (string?)await cmd.ExecuteScalarAsync();
        Assert.Equal("test", val);
    }

    [Fact]
    public async Task PluginConfigContext_MergesDefaultsAndUserOverrides_AndNotifies()
    {
        var configDir = Path.Combine(_tempDir, "config");
        var defaultsStream = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            { "endpointUrl": "http://localhost:8080", "modelId": "qwen3-8b", "timeout": 30 }
            """));

        string? broadcastedPlugin = null;
        JsonElement? broadcastedJson = null;

        var cfgCtx = new PluginConfigContext(
            "samplePlugin",
            configDir,
            assetName => assetName == "settings.defaults.json" ? defaultsStream() : null,
            (pId, json) => { broadcastedPlugin = pId; broadcastedJson = json; });

        var initial = cfgCtx.Get<TestSettings>();
        Assert.Equal("http://localhost:8080", initial.EndpointUrl);
        Assert.Equal("qwen3-8b", initial.ModelId);
        Assert.Equal(30, initial.Timeout);

        bool notified = false;
        using var sub = cfgCtx.OnConfigChanged<TestSettings>(updated =>
        {
            if (updated.ModelId == "custom-model")
                notified = true;
        });

        using var doc = JsonDocument.Parse("""{ "modelId": "custom-model" }""");
        await cfgCtx.UpdateAsync(doc.RootElement);

        Assert.True(notified);
        Assert.Equal("samplePlugin", broadcastedPlugin);
        Assert.NotNull(broadcastedJson);
        Assert.Equal("custom-model", cfgCtx.Get<TestSettings>().ModelId);
        Assert.Equal("http://localhost:8080", cfgCtx.Get<TestSettings>().EndpointUrl);

        await cfgCtx.ResetToDefaultsAsync();
        Assert.Equal("qwen3-8b", cfgCtx.Get<TestSettings>().ModelId);
    }

    [Fact]
    public async Task PluginConfigContext_DeepCloningAndCachedSnapshotPerformance()
    {
        var configDir = Path.Combine(_tempDir, "config-cache");
        var defaultsStream = () => new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            { "endpointUrl": "http://localhost:8080", "modelId": "qwen3-8b", "timeout": 30 }
            """));

        var cfgCtx = new PluginConfigContext(
            "cacheTestPlugin",
            configDir,
            assetName => assetName == "settings.defaults.json" ? defaultsStream() : null,
            (pId, json) => { });

        // Consecutive calls to GetRawJson return equivalent cached elements
        var raw1 = cfgCtx.GetRawJson();
        var raw2 = cfgCtx.GetRawJson();
        Assert.Equal(raw1.GetProperty("endpointUrl").GetString(), raw2.GetProperty("endpointUrl").GetString());
        Assert.Equal("qwen3-8b", raw1.GetProperty("modelId").GetString());

        // Update mutates snapshot
        using var doc = JsonDocument.Parse("""{ "timeout": 60 }""");
        await cfgCtx.UpdateAsync(doc.RootElement);

        var updatedRaw = cfgCtx.GetRawJson();
        Assert.Equal(60, updatedRaw.GetProperty("timeout").GetInt32());
        Assert.Equal("http://localhost:8080", updatedRaw.GetProperty("endpointUrl").GetString());

        // Reset cleans up user config file
        await cfgCtx.ResetToDefaultsAsync();
        Assert.Equal(30, cfgCtx.GetRawJson().GetProperty("timeout").GetInt32());
        string configFile = Path.Combine(configDir, "cacheTestPlugin.json");
        Assert.False(File.Exists(configFile));
    }

    [Fact]
    public void PluginModules_CanDeclareImmersivePlugins_Polymorphically()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var module = new ImmersiveReading.Plugins.PosHighlighter.PosHighlighterModule();
        module.ConfigureServices(services);
        var provider = services.BuildServiceProvider();

        var immersivePlugins = module.CreateImmersivePlugins(provider);
        Assert.Single(immersivePlugins);
        Assert.Equal(ImmersiveReading.Plugins.PosHighlighter.PosHighlighterPlugin.PluginIdValue, immersivePlugins[0].Manifest.Id);
    }

    [Fact]
    public void PythonEnvironmentResolver_ResolvesExtractorDirAndPythonExe()
    {
        var resolver = ImmersiveReading.Core.Infrastructure.PythonEnvironmentResolver.Instance;
        string? extractorDir = resolver.ResolveExtractorDir();
        Assert.NotNull(extractorDir);
        Assert.True(Directory.Exists(extractorDir));
        Assert.True(File.Exists(Path.Combine(extractorDir, "pyproject.toml")) || Directory.Exists(Path.Combine(extractorDir, "immersive")));

        string? pythonExe = resolver.ResolvePythonExe(extractorDir: extractorDir);
        Assert.False(string.IsNullOrWhiteSpace(pythonExe));
    }

    [Fact]
    public async Task PluginHostManager_LifecycleAndRpcDispatch()
    {
        using var languages = new LanguagePluginRegistry();
        var host = new PluginHostManager(
            _tempDir,
            new MockBookCatalog(),
            new MockReaderContextService(),
            new MockVocabBudgetService(),
            new MockSnippetEnricher(),
            languages,
            _events);

        var hotPlugin = new SampleTestPlugin("hot1", PluginReloadBehavior.Hot);
        var coldPlugin = new SampleTestPlugin("cold1", PluginReloadBehavior.RestartRequired);

        await host.InitializePluginsAsync([hotPlugin, coldPlugin]);

        Assert.True(host.TryGetRpcHandler("ext.hot1.ping", out var pingHandler));
        var res = await pingHandler(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.NotNull(res);

        // Hot Disable
        var (hotState, requiresRestartHot) = await host.SetPluginEnabledAsync("hot1", false);
        Assert.Equal(PluginRuntimeState.Disabled, hotState);
        Assert.False(requiresRestartHot);
        Assert.False(host.TryGetRpcHandler("ext.hot1.ping", out _));

        // Cold Disable
        var (coldState, requiresRestartCold) = await host.SetPluginEnabledAsync("cold1", false);
        Assert.Equal(PluginRuntimeState.PendingDisable, coldState);
        Assert.True(requiresRestartCold);

        await host.DisposeAsync();
    }

    private sealed class TestSettings
    {
        public string EndpointUrl { get; set; } = "";
        public string ModelId { get; set; } = "";
        public int Timeout { get; set; }
    }

    private sealed class SampleTestPlugin(string id, PluginReloadBehavior reload) : IImmersivePlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = id,
            Name = "Test " + id,
            Version = "1.0.0",
            ReloadBehavior = reload
        };

        public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
        {
            context.RegisterRpcAction("ping", (p, _) => Task.FromResult<object?>(new { pong = true }));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisableAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRpcEventSink : IRpcEventSink
    {
        public List<(string Event, object Data)> Emitted { get; } = [];
        public void Emit(string eventName, object data) => Emitted.Add((eventName, data));
    }

    private sealed class MockBookCatalog : IBookCatalog
    {
        public BookDetail? GetBookDetail(string bookId) => null;
        public BookSummary? GetBookSummary(string bookId) => null;
        public byte[]? GetCoverImageBytes(string bookId) => null;
        public string? GetEpubFilePath(string bookId) => null;
    }

    private sealed class MockReaderContextService : IReaderContextService
    {
        public Task<ReadPassageSlice> GetJustReadPassageAsync(string bookId, int maxCharacters = 3500, int? previousCheckpointTokenId = null, int? overrideViewportTokenId = null, int? overrideChapterIndex = null, string? overrideSpineHref = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ReadPassageSlice>> GetStoryContextUpToProgressAsync(string bookId, int? overrideChapterIndex = null, string? overrideSpineHref = null, int? overrideViewportTokenId = null, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class MockVocabBudgetService : IVocabularyBudgetService
    {
        public NPlusOneVocabularyProfile BuildNPlusOneProfile(string language, string? bookId, IReadOnlyCollection<string>? chapterTargetWords = null, int maxTurnTargets = 5) => throw new NotImplementedException();
        public VocabularyAuditResult AuditReplyTokens(IReadOnlyList<AnalyzedToken> replyTokens, NPlusOneVocabularyProfile profile) => throw new NotImplementedException();
    }

    private sealed class MockSnippetEnricher : ISnippetEnricher
    {
        public EnrichedSnippetResult EnrichSnippet(string bookId, string plainText, string language, IReadOnlySet<string>? targetPlusOneLemmas = null) => throw new NotImplementedException();
    }
}

