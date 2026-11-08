using System.Text.Json;
using AngleSharp.Html.Parser;
using ImmersiveReading.App.Plugins;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.PosHighlighter;
using Xunit;

namespace ImmersiveReading.App.Tests;

public class PosHighlighterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestRpcEventSink _events = new();

    public PosHighlighterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ir-pos-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void PosClassifier_Categorizes_Adjectives_Accurately()
    {
        // 1. i-Adjective
        var tokIAdj = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 4,
            Surface: "美味しい",
            Lemma: "美味しい",
            NormalizedForm: "美味しい",
            Reading: "おいしい",
            DictionaryReading: "おいしい",
            PartOfSpeech: "形容詞",
            ConjugationType: "形容詞・イ段",
            ConjugationForm: "基本形",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "自立"
        );
        var catI = PosClassifier.Classify(tokIAdj);
        Assert.Equal(PosCategory.AdjectiveI, catI);
        Assert.Equal("ir-pos-adj-i", PosClassifier.ToCssClass(catI));

        // 2. na-Adjective (IPAdic: 名詞 + 形容動詞語幹)
        var tokNaAdj = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "静か",
            Lemma: "静か",
            NormalizedForm: "静か",
            Reading: "しずか",
            DictionaryReading: "しずか",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "形容動詞語幹"
        );
        var catNa = PosClassifier.Classify(tokNaAdj);
        Assert.Equal(PosCategory.AdjectiveNa, catNa);
        Assert.Equal("ir-pos-adj-na", PosClassifier.ToCssClass(catNa));
    }

    [Fact]
    public void PosClassifier_Categorizes_Verbs_By_Conjugation()
    {
        // 1. Godan (u-verb)
        var tokGodan = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "書く",
            Lemma: "書く",
            NormalizedForm: "書く",
            Reading: "かく",
            DictionaryReading: "かく",
            PartOfSpeech: "動詞",
            ConjugationType: "五段・カ行イ音便",
            ConjugationForm: "基本形",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );
        var catGodan = PosClassifier.Classify(tokGodan);
        Assert.Equal(PosCategory.VerbGodan, catGodan);
        Assert.Equal("ir-pos-verb-u", PosClassifier.ToCssClass(catGodan));

        // 2. Ichidan (ru-verb)
        var tokIchidan = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 3,
            Surface: "食べる",
            Lemma: "食べる",
            NormalizedForm: "食べる",
            Reading: "たべる",
            DictionaryReading: "たべる",
            PartOfSpeech: "動詞",
            ConjugationType: "一段",
            ConjugationForm: "基本形",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );
        var catIchidan = PosClassifier.Classify(tokIchidan);
        Assert.Equal(PosCategory.VerbIchidan, catIchidan);
        Assert.Equal("ir-pos-verb-ru", PosClassifier.ToCssClass(catIchidan));

        // 3. Irregular verbs (suru / kuru)
        var tokSuru = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "する",
            Lemma: "する",
            NormalizedForm: "する",
            Reading: "する",
            DictionaryReading: "する",
            PartOfSpeech: "動詞",
            ConjugationType: "サ変・スル",
            ConjugationForm: "基本形",
            ReadingSource: RubySource.None,
            IsContentWord: true
        );
        var catSuru = PosClassifier.Classify(tokSuru);
        Assert.Equal(PosCategory.VerbIrregular, catSuru);
        Assert.Equal("ir-pos-verb-irr", PosClassifier.ToCssClass(catSuru));

        var tokKuru = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "来る",
            Lemma: "来る",
            NormalizedForm: "来る",
            Reading: "くる",
            DictionaryReading: "くる",
            PartOfSpeech: "動詞",
            ConjugationType: "カ変・クル",
            ConjugationForm: "基本形",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );
        var catKuru = PosClassifier.Classify(tokKuru);
        Assert.Equal(PosCategory.VerbIrregular, catKuru);
        Assert.Equal("ir-pos-verb-irr", PosClassifier.ToCssClass(catKuru));
    }

    [Fact]
    public void PosClassifier_Distinguishes_Suffixes_From_Particles()
    {
        // 1. Suffix (接尾辞 e.g. さん)
        var tokSuffix = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "さん",
            Lemma: "さん",
            NormalizedForm: "さん",
            Reading: "さん",
            DictionaryReading: "さん",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.None,
            IsContentWord: false,
            PartOfSpeechSub: "接尾"
        );
        var catSuffix = PosClassifier.Classify(tokSuffix);
        Assert.Equal(PosCategory.Suffix, catSuffix);
        Assert.Equal("ir-pos-suffix", PosClassifier.ToCssClass(catSuffix));

        // 2. Single-kana particle (e.g. は)
        var tokPrtSingle = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 1,
            Surface: "は",
            Lemma: "は",
            NormalizedForm: "は",
            Reading: "わ",
            DictionaryReading: "は",
            PartOfSpeech: "助詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.None,
            IsContentWord: false,
            PartOfSpeechSub: "係助詞"
        );
        var catSingle = PosClassifier.Classify(tokPrtSingle);
        Assert.Equal(PosCategory.ParticleSingle, catSingle);
        Assert.Equal("ir-pos-prt-single", PosClassifier.ToCssClass(catSingle));

        // 3. Multi-kana particle (e.g. から)
        var tokPrtMulti = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "から",
            Lemma: "から",
            NormalizedForm: "から",
            Reading: "から",
            DictionaryReading: "から",
            PartOfSpeech: "助詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.None,
            IsContentWord: false,
            PartOfSpeechSub: "格助詞"
        );
        var catMulti = PosClassifier.Classify(tokPrtMulti);
        Assert.Equal(PosCategory.ParticleMulti, catMulti);
        Assert.Equal("ir-pos-prt-multi", PosClassifier.ToCssClass(catMulti));
    }

    [Fact]
    public void PosDomEnrichmentStage_Enriches_AngleSharpDocument_With_PosClasses_And_Styles()
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument("""
            <!DOCTYPE html>
            <html>
            <head><title>Test</title></head>
            <body>
              <p>
                <span class="ir-tok" data-tid="0">田中</span>
                <span class="ir-tok" data-tid="1">さん</span>
                <span class="ir-tok" data-tid="2">は</span>
                <span class="ir-tok" data-tid="3">リンゴ</span>
                <span class="ir-tok" data-tid="4">を</span>
                <span class="ir-tok" data-tid="5">食べる</span>
              </p>
            </body>
            </html>
            """);

        var tokens = new List<EnrichedTokenEntry>
        {
            new(0, 0, new AnalyzedToken(0, 2, "田中", "田中", "田中", "たなか", "たなか", "名詞", "", "", RubySource.None, true), [], WordStatus.Known, false),
            new(1, 0, new AnalyzedToken(2, 4, "さん", "さん", "さん", "さん", "さん", "名詞", "", "", RubySource.None, false, PartOfSpeechSub: "接尾"), [], WordStatus.Known, false),
            new(2, 0, new AnalyzedToken(4, 5, "は", "は", "は", "わ", "は", "助詞", "", "", RubySource.None, false), [], WordStatus.Known, false),
            new(3, 0, new AnalyzedToken(5, 8, "リンゴ", "林檎", "リンゴ", "りんご", "りんご", "名詞", "", "", RubySource.None, true), [], WordStatus.New, false),
            new(4, 0, new AnalyzedToken(8, 9, "を", "を", "を", "お", "を", "助詞", "", "", RubySource.None, false), [], WordStatus.Known, false),
            new(5, 0, new AnalyzedToken(9, 12, "食べる", "食べる", "食べる", "たべる", "たべる", "動詞", "一段", "基本形", RubySource.None, true), [], WordStatus.Known, false),
        };

        var stage = new PosDomEnrichmentStage(new PosHighlighterSettings());
        var context = new DomEnrichmentContext("book-1", "chapter-1.xhtml", tokens, new EnrichmentOptions());

        stage.Enrich(doc, context);

        // Verify suffix
        var suffixEl = doc.QuerySelector(".ir-tok[data-tid='1']");
        Assert.NotNull(suffixEl);
        Assert.Contains("ir-pos-suffix", suffixEl.ClassList);
        Assert.Equal("suffix", suffixEl.GetAttribute("data-ir-pos"));

        // Verify single particle
        var particleEl = doc.QuerySelector(".ir-tok[data-tid='2']");
        Assert.NotNull(particleEl);
        Assert.Contains("ir-pos-prt-single", particleEl.ClassList);

        // Verify Ichidan verb
        var verbEl = doc.QuerySelector(".ir-tok[data-tid='5']");
        Assert.NotNull(verbEl);
        Assert.Contains("ir-pos-verb-ru", verbEl.ClassList);
        Assert.Equal("verb-ru", verbEl.GetAttribute("data-ir-pos"));

        // Verify style tag injected
        var styleEl = doc.QuerySelector("head style#ir-pos-highlighter-style");
        Assert.NotNull(styleEl);
        Assert.Contains(".ir-pos-verb-ru", styleEl.TextContent);
        Assert.Contains(".ir-pos-suffix", styleEl.TextContent);
        Assert.Contains(".ir-pos-prt-single", styleEl.TextContent);
    }

    [Fact]
    public async Task PosHighlighterPlugin_Lifecycle_And_HotReload()
    {
        var langRegistry = new LanguagePluginRegistry();
        var host = new PluginHostManager(
            dataRootDir: _tempDir,
            bookCatalog: new MockBookCatalog(),
            readerContext: new MockReaderContextService(),
            vocabBudget: new MockVocabBudgetService(),
            snippetEnricher: new MockSnippetEnricher(),
            languageRegistry: langRegistry,
            rpcEvents: _events);

        var plugin = new PosHighlighterPlugin();
        await host.InitializePluginsAsync([plugin]);

        // 1. Initial state: enabled
        Assert.Single(host.ActiveDomStages);
        Assert.Equal("pos-highlighter", host.ActiveDomStages[0].StageId);

        // 2. RPC getSettings
        var hasHandler = host.TryGetRpcHandler("ext.posHighlighter.getSettings", out var getHandler);
        Assert.True(hasHandler);
        Assert.NotNull(getHandler);

        var settingsObj = await getHandler!(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.NotNull(settingsObj);
        Assert.True(((PosHighlighterSettings)settingsObj).Enabled);

        // 3. RPC toggle
        var hasToggle = host.TryGetRpcHandler("ext.posHighlighter.toggle", out var toggleHandler);
        Assert.True(hasToggle);
        Assert.NotNull(toggleHandler);

        await toggleHandler!(JsonDocument.Parse("{}").RootElement, CancellationToken.None);
        Assert.False(plugin.CurrentSettings.Enabled);

        // 4. Hot disable via manager
        var disableResult = await host.SetPluginEnabledAsync("posHighlighter", false);
        Assert.Equal(PluginRuntimeState.Disabled, disableResult.State);
        Assert.Empty(host.ActiveDomStages);

        // 5. Hot enable via manager
        var enableResult = await host.SetPluginEnabledAsync("posHighlighter", true);
        Assert.Equal(PluginRuntimeState.Enabled, enableResult.State);
        Assert.Single(host.ActiveDomStages);

        await host.DisposeAsync();
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
