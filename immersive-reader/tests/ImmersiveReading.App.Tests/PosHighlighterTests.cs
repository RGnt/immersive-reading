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

        // 3. na-Adjective with UniDic 形状詞可能 (e.g. 便利, 親切)
        var tokBenri = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "便利",
            Lemma: "便利",
            NormalizedForm: "便利",
            Reading: "べんり",
            DictionaryReading: "べんり",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "普通名詞,形状詞可能,*"
        );
        var catBenri = PosClassifier.Classify(tokBenri);
        Assert.Equal(PosCategory.AdjectiveNa, catBenri);
        Assert.Equal("ir-pos-adj-na", PosClassifier.ToCssClass(catBenri));

        // 4. na-Adjective UniDic 形状詞 (e.g. 特別, 重要)
        var tokJuyo = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "重要",
            Lemma: "重要",
            NormalizedForm: "重要",
            Reading: "じゅうよう",
            DictionaryReading: "じゅうよう",
            PartOfSpeech: "形状詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "一般"
        );
        var catJuyo = PosClassifier.Classify(tokJuyo);
        Assert.Equal(PosCategory.AdjectiveNa, catJuyo);
        Assert.Equal("ir-pos-adj-na", PosClassifier.ToCssClass(catJuyo));
    }

    [Fact]
    public void PosClassifier_Categorizes_Kanji_Adverbs_And_Nouns_Accurately()
    {
        // 1. Kanji Adverb with UniDic 副詞可能 (e.g. 時々, 毎日)
        var tokTokidoki = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "時々",
            Lemma: "時々",
            NormalizedForm: "時々",
            Reading: "ときどき",
            DictionaryReading: "ときどき",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "普通名詞,副詞可能,*"
        );
        var catTokidoki = PosClassifier.Classify(tokTokidoki);
        Assert.Equal(PosCategory.Adverb, catTokidoki);
        Assert.Equal("ir-pos-adverb", PosClassifier.ToCssClass(catTokidoki));

        // 2. Pure Kanji Adverb (e.g. 突然)
        var tokTotsuzen = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "突然",
            Lemma: "突然",
            NormalizedForm: "突然",
            Reading: "とつぜん",
            DictionaryReading: "とつぜん",
            PartOfSpeech: "副詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );
        var catTotsuzen = PosClassifier.Classify(tokTotsuzen);
        Assert.Equal(PosCategory.Adverb, catTotsuzen);

        // 3. Kanji Prefix (e.g. 超)
        var tokCho = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 1,
            Surface: "超",
            Lemma: "超",
            NormalizedForm: "超",
            Reading: "ちょう",
            DictionaryReading: "ちょう",
            PartOfSpeech: "接頭辞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );
        var catCho = PosClassifier.Classify(tokCho);
        Assert.Equal(PosCategory.Suffix, catCho);

        // 4. Pure Kanji Noun (e.g. 学校)
        var tokGakko = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "学校",
            Lemma: "学校",
            NormalizedForm: "学校",
            Reading: "がっこう",
            DictionaryReading: "がっこう",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true,
            PartOfSpeechSub: "普通名詞,一般"
        );
        var catGakko = PosClassifier.Classify(tokGakko);
        Assert.Equal(PosCategory.Noun, catGakko);
        Assert.Equal("ir-pos-noun", PosClassifier.ToCssClass(catGakko));
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
        Assert.Contains(".ir-pos-noun", styleEl.TextContent);
        // Verify furigana preserves reader ruby color and is not colored with highlight color
        Assert.Contains("rt { color: var(--ruby-color, #e2e8f0) !important;", styleEl.TextContent);
        Assert.Contains(".ir-pos-verb-ru rt", styleEl.TextContent);
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

    [Fact]
    public void PosDomEnrichmentStage_Applies_WordType_And_SubType_Span_Classes()
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument("""
            <!DOCTYPE html>
            <html>
            <head><title>Test</title></head>
            <body>
              <p>
                <span class="ir-tok" data-tid="0">美味しい</span>
                <span class="ir-tok" data-tid="1">リンゴ</span>
                <span class="ir-tok" data-tid="2">を</span>
                <span class="ir-tok" data-tid="3">食べる</span>
                <span class="ir-tok" data-tid="4">田中</span>
                <span class="ir-tok" data-tid="5">さん</span>
              </p>
            </body>
            </html>
            """);

        var tokens = new List<EnrichedTokenEntry>
        {
            new(0, 0, new AnalyzedToken(0, 4, "美味しい", "美味しい", "美味しい", "おいしい", "おいしい", "形容詞", "形容詞", "", RubySource.None, true), [], WordStatus.Known, false),
            new(1, 0, new AnalyzedToken(4, 7, "リンゴ", "林檎", "リンゴ", "りんご", "りんご", "名詞", "", "", RubySource.None, true), [], WordStatus.New, false),
            new(2, 0, new AnalyzedToken(7, 8, "を", "を", "を", "お", "を", "助詞", "", "", RubySource.None, false), [], WordStatus.Known, false),
            new(3, 0, new AnalyzedToken(8, 11, "食べる", "食べる", "食べる", "たべる", "たべる", "動詞", "一段", "基本形", RubySource.None, true), [], WordStatus.Known, false),
            new(4, 0, new AnalyzedToken(11, 13, "田中", "田中", "田中", "たなか", "たなか", "名詞", "", "", RubySource.None, true), [], WordStatus.Known, false),
            new(5, 0, new AnalyzedToken(13, 15, "さん", "さん", "さん", "さん", "さん", "名詞", "", "", RubySource.None, false, PartOfSpeechSub: "接尾"), [], WordStatus.Known, false),
        };

        var cacheDb = Path.Combine(_tempDir, "pos_test_cache.db");
        using var cache = new ChapterPosCache(cacheDb, maxCacheSizeMb: 500);
        var stage = new PosDomEnrichmentStage(new PosHighlighterSettings(), cache: cache);
        var context = new DomEnrichmentContext("test-book", "ch1.xhtml", tokens, new EnrichmentOptions());

        stage.Enrich(doc, context);

        // 1. Adjective: class "adjective", "adjective-i", "ir-pos-adj-i", data-word-type="adjective"
        var adjEl = doc.QuerySelector(".ir-tok[data-tid='0']");
        Assert.NotNull(adjEl);
        Assert.Contains("adjective", adjEl.ClassList);
        Assert.Contains("adjective-i", adjEl.ClassList);
        Assert.Contains("ir-pos-adj-i", adjEl.ClassList);
        Assert.Equal("adjective", adjEl.GetAttribute("data-word-type"));

        // 2. Noun: class "noun", "ir-pos-noun", data-word-type="noun"
        var nounEl = doc.QuerySelector(".ir-tok[data-tid='1']");
        Assert.NotNull(nounEl);
        Assert.Contains("noun", nounEl.ClassList);
        Assert.Equal("noun", nounEl.GetAttribute("data-word-type"));

        // 3. Particle: class "particle", "particle-single", "ir-pos-prt-single", data-word-type="particle"
        var prtEl = doc.QuerySelector(".ir-tok[data-tid='2']");
        Assert.NotNull(prtEl);
        Assert.Contains("particle", prtEl.ClassList);
        Assert.Contains("particle-single", prtEl.ClassList);
        Assert.Contains("ir-pos-prt-single", prtEl.ClassList);
        Assert.Equal("particle", prtEl.GetAttribute("data-word-type"));

        // 4. Verb: class "verb", "verb-ru", "ir-pos-verb-ru", data-word-type="verb"
        var verbEl = doc.QuerySelector(".ir-tok[data-tid='3']");
        Assert.NotNull(verbEl);
        Assert.Contains("verb", verbEl.ClassList);
        Assert.Contains("verb-ru", verbEl.ClassList);
        Assert.Contains("ir-pos-verb-ru", verbEl.ClassList);
        Assert.Equal("verb", verbEl.GetAttribute("data-word-type"));

        // 5. Suffix: class "suffix", "ir-pos-suffix", data-word-type="suffix"
        var sfxEl = doc.QuerySelector(".ir-tok[data-tid='5']");
        Assert.NotNull(sfxEl);
        Assert.Contains("suffix", sfxEl.ClassList);
        Assert.Contains("ir-pos-suffix", sfxEl.ClassList);
        Assert.Equal("suffix", sfxEl.GetAttribute("data-word-type"));
    }

    [Fact]
    public void PosDomEnrichmentStage_Surrounds_Raw_Paragraph_Text_With_Sudachi_Morpheme_Spans()
    {
        var parser = new HtmlParser();
        // A raw book chapter with unsegmented Japanese text
        var doc = parser.ParseDocument("""
            <!DOCTYPE html>
            <html>
            <head><title>Raw Chapter</title></head>
            <body>
              <p>魔女たちの茶会が開かれる。</p>
            </body>
            </html>
            """);

        var cacheDb = Path.Combine(_tempDir, "pos_raw_test_cache.db");
        using var cache = new ChapterPosCache(cacheDb, maxCacheSizeMb: 500);
        var stage = new PosDomEnrichmentStage(new PosHighlighterSettings(), cache: cache);
        var context = new DomEnrichmentContext("book-raw", "ch1.xhtml", [], new EnrichmentOptions());

        stage.Enrich(doc, context);

        // Verify that raw text was surrounded with .ir-tok spans
        var spans = doc.QuerySelectorAll("p .ir-tok");
        Assert.NotEmpty(spans);

        // Find suffix 'たち'
        var suffixEl = spans.FirstOrDefault(s => s.TextContent == "たち");
        Assert.NotNull(suffixEl);
        Assert.Contains("suffix", suffixEl.ClassList);
        Assert.Equal("suffix", suffixEl.GetAttribute("data-word-type"));

        // Find single particle 'の'
        var particleEl = spans.FirstOrDefault(s => s.TextContent == "の");
        Assert.NotNull(particleEl);
        Assert.Contains("particle", particleEl.ClassList);
        Assert.Contains("particle-single", particleEl.ClassList);
        Assert.Equal("particle", particleEl.GetAttribute("data-word-type"));

        // Find verb '開か'
        var verbEl = spans.FirstOrDefault(s => s.TextContent == "開か");
        Assert.NotNull(verbEl);
        Assert.Contains("verb", verbEl.ClassList);
        Assert.Contains("verb-u", verbEl.ClassList);
    }

    [Fact]
    public void ChapterPosCache_Stores_Retrieves_And_Reports_Stats()
    {
        var cacheDb = Path.Combine(_tempDir, "pos_lru_cache.db");
        using var cache = new ChapterPosCache(cacheDb, maxCacheSizeMb: 500);

        Assert.Equal(500, cache.MaxCacheSizeMb);
        Assert.Null(cache.Get("b1", "ch1.xhtml", "hash-1"));

        string payload = """{"bookId":"b1","chapterHref":"ch1.xhtml","classifications":[{"id":0,"wordType":"verb","subType":"verb-u","code":"verb-u","classes":["verb","verb-u"]}]}""";
        cache.Set("b1", "ch1.xhtml", "hash-1", payload);

        var retrieved = cache.Get("b1", "ch1.xhtml", "hash-1");
        Assert.NotNull(retrieved);
        Assert.Contains("verb-u", retrieved);

        var (count, bytes) = cache.GetStats();
        Assert.Equal(1, count);
        Assert.True(bytes > 0);

        cache.Clear();
        var (countAfter, bytesAfter) = cache.GetStats();
        Assert.Equal(0, countAfter);
        Assert.Equal(0, bytesAfter);
    }

    [Fact]
    public void PythonChapterPosTagger_Parses_Classifications_Json_Accurately()
    {
        string sampleJson = """
        {
          "bookId": "b1",
          "chapterHref": "ch1.xhtml",
          "classifications": [
            {
              "id": 10,
              "wordType": "adjective",
              "subType": "adjective-i",
              "code": "adj-i",
              "classes": ["adjective", "adjective-i", "ir-pos-adj-i"]
            },
            {
              "id": 11,
              "wordType": "particle",
              "subType": "particle-single",
              "code": "prt-single",
              "classes": ["particle", "particle-single", "ir-pos-prt-single"]
            }
          ]
        }
        """;

        var parsed = PythonChapterPosTagger.ParseClassificationsJson(sampleJson);
        Assert.Equal(2, parsed.Count);
        Assert.True(parsed.ContainsKey(10));
        Assert.Equal("adjective", parsed[10].WordType);
        Assert.Equal("adjective-i", parsed[10].SubType);
        Assert.Contains("adjective", parsed[10].Classes);

        Assert.True(parsed.ContainsKey(11));
        Assert.Equal("particle", parsed[11].WordType);
        Assert.Equal("particle-single", parsed[11].SubType);
    }

    [Fact]
    public void PythonChapterPosTagger_Invokes_Python_And_Returns_Classifications()
    {
        var tagger = new PythonChapterPosTagger();
        string? py = tagger.ResolvePythonExe();
        string? ex = tagger.ResolveExtractorDir();
        Assert.True(File.Exists(py), $"Python not found: {py}, extractorDir: {ex}");

        var tokens = new List<EnrichedTokenEntry>
        {
            new(0, 0, new AnalyzedToken(0, 2, "猫", "猫", "猫", "ねこ", "ねこ", "名詞", "", "", RubySource.None, true), [], WordStatus.New, false),
            new(1, 0, new AnalyzedToken(2, 3, "が", "が", "が", "が", "が", "助詞", "", "", RubySource.None, false), [], WordStatus.Known, false),
            new(2, 0, new AnalyzedToken(3, 7, "速い", "速い", "速い", "はやい", "はやい", "形容詞", "形容詞", "", RubySource.None, true), [], WordStatus.Known, false),
        };

        var (dict, raw) = tagger.TagChapter("test-book", "ch1.xhtml", tokens);
        Assert.True(dict != null, $"TagChapter returned null. raw: {raw}");
        Assert.Equal(3, dict.Count);
        Assert.Equal("noun", dict[0].WordType);
        Assert.Equal("particle", dict[1].WordType);
        Assert.Equal("adjective", dict[2].WordType);
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
