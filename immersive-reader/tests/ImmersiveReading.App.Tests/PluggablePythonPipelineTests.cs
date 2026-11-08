namespace ImmersiveReading.App.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ImmersiveReading.App.Plugins;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.Japanese;
using Xunit;

public sealed class PluggablePythonPipelineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LanguagePluginRegistry _languages;
    private readonly TestRpcEventSink _events;
    private readonly PluginHostManager _host;

    public PluggablePythonPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ir_pipeline_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _languages = new LanguagePluginRegistry();
        _events = new TestRpcEventSink();

        _host = new PluginHostManager(
            dataRootDir: _tempDir,
            bookCatalog: new MockBookCatalog(),
            readerContext: new MockReaderContextService(),
            vocabBudget: new MockVocabBudgetService(),
            snippetEnricher: new MockSnippetEnricher(),
            languageRegistry: _languages,
            rpcEvents: _events);
    }

    public void Dispose()
    {
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task JapaneseLanguagePlugin_RegistersFourPythonStages_SortedByOrder()
    {
        var jaPlugin = new JapaneseLanguagePlugin();
        await _host.InitializePluginsAsync([jaPlugin]);

        var jaStages = await _host.ResolveStagesForLanguageAsync("ja");

        Assert.Equal(4, jaStages.Count);
        Assert.Equal("ja.sudachi_tokenize", jaStages[0].Descriptor.StageId);
        Assert.Equal(110, jaStages[0].Descriptor.Order);
        Assert.Equal("ja.kanji_enrichment", jaStages[1].Descriptor.StageId);
        Assert.Equal(210, jaStages[1].Descriptor.Order);
        Assert.Equal("ja.grammar", jaStages[2].Descriptor.StageId);
        Assert.Equal(310, jaStages[2].Descriptor.Order);
        Assert.Equal("ja.pdf_sections", jaStages[3].Descriptor.StageId);
        Assert.Equal(410, jaStages[3].Descriptor.Order);

        // Verify that the python script was materialized from embedded resources into plugin data directory
        var scriptPath = Path.Combine(jaStages[0].PluginDataDirectory, "python", "ja_stages.py");
        Assert.True(File.Exists(scriptPath), $"Expected script at {scriptPath}");
        var scriptContent = await File.ReadAllTextAsync(scriptPath);
        Assert.Contains("JapaneseSudachiTokenizerStage", scriptContent);
    }

    [Fact]
    public async Task ResolveStagesForLanguage_FiltersCorrectly_ByLanguageCode()
    {
        var jaPlugin = new JapaneseLanguagePlugin();
        var dummyEsPlugin = new DummySpanishPlugin();
        await _host.InitializePluginsAsync([jaPlugin, dummyEsPlugin]);

        var jaStages = await _host.ResolveStagesForLanguageAsync("ja");
        var esStages = await _host.ResolveStagesForLanguageAsync("es");
        var frStages = await _host.ResolveStagesForLanguageAsync("fr");

        Assert.Equal(4, jaStages.Count);
        Assert.All(jaStages, s => Assert.Equal("japanese", s.PluginId));

        Assert.Single(esStages);
        Assert.Equal("spanish", esStages[0].PluginId);
        Assert.Equal("es.morphology", esStages[0].Descriptor.StageId);

        Assert.Empty(frStages);
    }

    [Fact]
    public void PythonPipelineRunManifest_SerializesExpectedJsonStructure()
    {
        var manifest = new PythonPipelineRunManifest
        {
            BookId = "book-12345",
            Title = "Test Book",
            Language = "ja",
            EpubFilePath = "C:/books/test.epub",
            OutputDirectory = "C:/output/book-12345",
            IncludeA4Pdf = true,
            ExtraPythonPaths = ["C:/site-packages"],
            Stages =
            [
                new ResolvedPythonStageManifestEntry
                {
                    PluginId = "japanese",
                    StageId = "ja.sudachi_tokenize",
                    Order = 110,
                    ScriptFilePath = "C:/plugins/japanese/python/ja_stages.py",
                    EntryClassName = "JapaneseSudachiTokenizerStage",
                    PluginDataDir = "C:/plugins/japanese"
                }
            ]
        };

        var json = JsonSerializer.Serialize(manifest);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("book-12345", root.GetProperty("bookId").GetString());
        Assert.Equal("ja", root.GetProperty("language").GetString());
        Assert.True(root.GetProperty("includeA4Pdf").GetBoolean());
        var stages = root.GetProperty("stages");
        Assert.Equal(1, stages.GetArrayLength());
        Assert.Equal("ja.sudachi_tokenize", stages[0].GetProperty("stageId").GetString());
        Assert.Equal(110, stages[0].GetProperty("order").GetInt32());
    }

    private sealed class DummySpanishPlugin : IImmersivePlugin
    {
        public PluginManifest Manifest { get; } = new()
        {
            Id = "spanish",
            Name = "Spanish Language Pack",
            Version = "1.0.0",
            Author = "Community",
            Description = "Spanish test pack",
            ReloadBehavior = PluginReloadBehavior.Hot
        };

        public ValueTask OnEnableAsync(IPluginHostContext context, System.Threading.CancellationToken ct = default)
        {
            context.RegisterPythonStage(new PythonStageDescriptor
            {
                StageId = "es.morphology",
                Order = 110,
                TargetLanguages = ["es"],
                ScriptRelativePath = "python/es_dummy.py",
                EntryClassName = "SpanishMorphologyStage"
            });
            return ValueTask.CompletedTask;
        }

        public ValueTask OnDisableAsync(System.Threading.CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestRpcEventSink : IRpcEventSink
    {
        public System.Collections.Generic.List<(string Event, object Data)> Emitted { get; } = [];
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
        public Task<ReadPassageSlice> GetJustReadPassageAsync(string bookId, int maxCharacters = 3500, int? previousCheckpointTokenId = null, int? overrideViewportTokenId = null, int? overrideChapterIndex = null, string? overrideSpineHref = null, System.Threading.CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Collections.Generic.IReadOnlyList<ReadPassageSlice>> GetStoryContextUpToProgressAsync(string bookId, int? overrideChapterIndex = null, string? overrideSpineHref = null, int? overrideViewportTokenId = null, System.Threading.CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class MockVocabBudgetService : IVocabularyBudgetService
    {
        public NPlusOneVocabularyProfile BuildNPlusOneProfile(string language, string? bookId, System.Collections.Generic.IReadOnlyCollection<string>? chapterTargetWords = null, int maxTurnTargets = 5) => throw new NotImplementedException();
        public VocabularyAuditResult AuditReplyTokens(System.Collections.Generic.IReadOnlyList<AnalyzedToken> replyTokens, NPlusOneVocabularyProfile profile) => throw new NotImplementedException();
    }

    private sealed class MockSnippetEnricher : ISnippetEnricher
    {
        public EnrichedSnippetResult EnrichSnippet(string bookId, string plainText, string language, System.Collections.Generic.IReadOnlySet<string>? targetPlusOneLemmas = null) => throw new NotImplementedException();
    }
}
