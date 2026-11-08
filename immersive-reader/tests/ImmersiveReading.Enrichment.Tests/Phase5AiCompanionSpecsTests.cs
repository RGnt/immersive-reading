using System.Text.Json;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.AiCompanion;
using ImmersiveReading.Plugins.Japanese;
using Xunit;

namespace ImmersiveReading.Enrichment.Tests;

public sealed class Phase5AiCompanionSpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _library;
    private readonly LanguagePluginRegistry _pluginRegistry;
    private readonly JapaneseLanguagePlugin _jaPlugin;
    private readonly ChapterEnrichmentCache _cache;
    private readonly ReaderContextService _readerContext;
    private readonly SnippetEnricher _snippetEnricher;
    private readonly VocabularyBudgetService _vocabBudget;

    public Phase5AiCompanionSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-phase5-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new LibraryDatabase(Path.Combine(_tempDir, "library.db"), Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _library = new LibraryManager(_db);

        _pluginRegistry = new LanguagePluginRegistry();
        _jaPlugin = new JapaneseLanguagePlugin();
        _pluginRegistry.Register(_jaPlugin);
        _pluginRegistry.Register(new WhitespaceLanguagePlugin(), isFallback: true);

        _cache = new ChapterEnrichmentCache(maxEntries: 20);
        _readerContext = new ReaderContextService(_library, _cache, _db);
        _snippetEnricher = new SnippetEnricher(_pluginRegistry, _cache);
        _vocabBudget = new VocabularyBudgetService(_db);
    }

    public void Dispose()
    {
        _pluginRegistry.Dispose();
        _db.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task Scenario1_ReaderContextService_ExtractsJustReadPassage_StrictlyUpToViewportToken()
    {
        string bookId = "book-4e0541b184c4ace9";
        string ch2Href = "xhtml/ch2.xhtml";
        string ch3Href = "xhtml/ch3.xhtml";

        // Build Chapter 2 with 900 tokens (0..899) and Chapter 3 with spoiler tokens
        var ch2Tokens = new List<EnrichedTokenEntry>();
        for (int i = 0; i < 900; i++)
        {
            string surface = i <= 320 ? $"語{i}" : $"未来ネタバレ{i}";
            ch2Tokens.Add(new EnrichedTokenEntry(
                TokenId: i,
                ParagraphId: i / 50,
                Token: new AnalyzedToken(i * 2, i * 2 + 2, surface, surface, surface, "ご", "ご", "名詞", "*", "*", RubySource.PluginGenerated, true),
                RubyUnits: []));
        }
        _cache.Set(new ChapterTokenMap(bookId, ch2Href, ch2Tokens, ["段落テキスト"]));

        var ch3Tokens = new List<EnrichedTokenEntry>
        {
            new(0, 0, new AnalyzedToken(0, 5, "第3章の秘密", "第3章の秘密", "第3章の秘密", "ひみつ", "ひみつ", "名詞", "*", "*", RubySource.PluginGenerated, true), [])
        };
        _cache.Set(new ChapterTokenMap(bookId, ch3Href, ch3Tokens, ["第3章の秘密"]));

        var slice = await _readerContext.GetJustReadPassageAsync(
            bookId: bookId,
            maxCharacters: 10000,
            previousCheckpointTokenId: 100,
            overrideViewportTokenId: 320,
            overrideChapterIndex: 2,
            overrideSpineHref: ch2Href);

        Assert.Equal(2, slice.ChapterIndex);
        Assert.Equal(100, slice.StartTokenId);
        Assert.Equal(320, slice.EndTokenId);
        Assert.Contains("語100", slice.PlainText);
        Assert.Contains("語320", slice.PlainText);
        Assert.DoesNotContain("語99", slice.PlainText);
        Assert.DoesNotContain("未来ネタバレ321", slice.PlainText);
        Assert.DoesNotContain("第3章の秘密", slice.PlainText);
    }

    [Fact]
    public async Task Scenario2_And_Scenario3_ComprehensionQuizService_McqToFreeFormProgression_And_EnrichedSnippets()
    {
        string bookId = "book-4e0541b184c4ace9";
        string ch2Href = "xhtml/ch2.xhtml";

        var enrichedCh = AngleSharpDomEnricher.EnrichChapter(
            "<html><body><p>クークは海辺で焚き火の準備を始めました。</p></body></html>",
            bookId,
            ch2Href,
            _jaPlugin,
            new EnrichmentOptions(FuriganaMode.AllKanji));
        _cache.Set(enrichedCh.TokenMap);

        var mockLlm = new MockScriptedLlmProvider();
        var quizService = new ComprehensionQuizService(_db, _readerContext, _snippetEnricher, mockLlm);

        // Stage 1: Initial stage is MultipleChoice
        Assert.Equal(ComprehensionStage.MultipleChoice, quizService.GetCurrentStage(bookId));

        var mcqQuestion = await quizService.GenerateQuestionAsync(
            bookId: bookId,
            previousCheckpointTokenId: 0,
            overrideViewportTokenId: 20,
            overrideChapterIndex: 2,
            overrideSpineHref: ch2Href);

        Assert.Equal(ComprehensionStage.MultipleChoice, mcqQuestion.Stage);
        Assert.Equal(4, mcqQuestion.Options.Count);
        Assert.Contains("ir-tok", mcqQuestion.PromptHtml);
        Assert.All(mcqQuestion.Options, o => Assert.Contains("ir-tok", o.EnrichedHtml));

        var mcqEval = await quizService.SubmitAnswerAsync(mcqQuestion.QuestionId, "0", selectedOptionIndex: 0);
        Assert.True(mcqEval.IsCorrect);
        Assert.Equal(100, mcqEval.ComprehensionScore);
        Assert.Contains("焚き火", mcqEval.EvidenceQuote);
        Assert.Contains("Jump to Paragraph", mcqEval.JumpToParagraphLabel);

        // Scenario 3: Record 4 more answers averaging >= 80% so total last 5 average is >= 80%
        for (int i = 0; i < 4; i++)
        {
            quizService.RecordHistoricalScoreForTesting(bookId, ComprehensionStage.MultipleChoice, 0.90);
        }

        Assert.Equal(ComprehensionStage.FreeForm, quizService.GetCurrentStage(bookId));

        var freeFormQuestion = await quizService.GenerateQuestionAsync(
            bookId: bookId,
            previousCheckpointTokenId: 0,
            overrideViewportTokenId: 20,
            overrideChapterIndex: 2,
            overrideSpineHref: ch2Href);

        Assert.Equal(ComprehensionStage.FreeForm, freeFormQuestion.Stage);
        Assert.Equal("freeform", freeFormQuestion.QuestionMode);

        var ffEval = await quizService.SubmitAnswerAsync(
            freeFormQuestion.QuestionId,
            "クークは海辺で焚き火の準備をしていました。");

        Assert.Equal(92, ffEval.ComprehensionScore);
        Assert.Equal("クークは海辺で焚き火の準備を始めました。", ffEval.EvidenceQuote);
        Assert.False(string.IsNullOrWhiteSpace(ffEval.MissingOrMisunderstood));
        Assert.Contains("ir-tok", ffEval.ModelAnswerHtml);
        Assert.Contains("<ruby", ffEval.ModelAnswerHtml);
    }

    [Fact]
    public async Task Scenario4_And_Scenario5_PersonaChatService_SpoilerCap_And_MeCabVerifiedNPlusOneRewrite()
    {
        string bookId = "book-4e0541b184c4ace9";
        string ch2Href = "xhtml/ch2.xhtml";

        // Seed chapter 2 tokens (0..400) where tokens > 320 contain a spoiler
        var tokens = new List<EnrichedTokenEntry>();
        for (int i = 0; i <= 400; i++)
        {
            string surf = i <= 320 ? "海辺" : "最終章の秘密";
            tokens.Add(new EnrichedTokenEntry(
                i,
                0,
                new AnalyzedToken(i * 2, i * 2 + 2, surf, surf, surf, "うみべ", "うみべ", "名詞", "*", "*", RubySource.PluginGenerated, true),
                []));
        }
        _cache.Set(new ChapterTokenMap(bookId, ch2Href, tokens, ["海辺"]));

        // Seed Known words (n set) and Learning words (+1 set: 焚き火, テント, 海岸)
        var tracker = new VocabularyTracker(_db);
        string[] knownWords = ["海", "行く", "準備", "楽しい", "一緒", "考える", "少し", "迷う", "火", "囲む", "暖かい"];
        foreach (var kw in knownWords)
        {
            tracker.SetStatus("ja", kw, kw, WordStatus.Known);
        }

        string[] targetPlusOne = ["焚き火", "テント", "海岸"];
        foreach (var tw in targetPlusOne)
        {
            tracker.SetStatus("ja", tw, tw, WordStatus.Learning);
        }

        var mockLlm = new MockScriptedLlmProvider();
        var personaService = new PersonaChatService(
            _db,
            _readerContext,
            _vocabBudget,
            _snippetEnricher,
            _pluginRegistry,
            mockLlm);

        // Scenario 4: Create BookPersona for "クーク" from highlighted dialogue
        var persona = personaService.CreatePersona(
            bookId: bookId,
            name: "クーク",
            roleSummary: "こぐまの主人公",
            speechStyle: "一人称は「ぼく」。親しみやすい口調。",
            voiceSamples: ["「ぼくにとって、うきわは海の中で、はちみつより、だいじかもしれない」"]);

        // Scenario 5: Send message while reading progress is at Chapter 2, tokenId 320
        var turn = await personaService.SendMessageAsync(
            personaId: persona.PersonaId,
            userMessage: "これから何をするの？",
            chapterTargetWords: targetPlusOne,
            overrideChapterIndex: 2,
            overrideSpineHref: ch2Href,
            overrideViewportTokenId: 320);

        // Verify Spoiler Cap at Chapter 2, tokenId 320
        Assert.Equal(2, turn.CappedChapterIndex);
        Assert.Equal(320, turn.CappedTokenId);
        Assert.DoesNotContain("最終章の秘密", turn.StoryContextSummary);

        // Verify MeCab detected the 3 out-of-budget words ["漸次", "趨勢", "躊躇"] in the initial draft and triggered rewrite
        Assert.True(turn.TriggeredNPlusOneRewrite);
        Assert.Contains("漸次", turn.InitialOutOfBudgetLemmas);
        Assert.Contains("趨勢", turn.InitialOutOfBudgetLemmas);
        Assert.Contains("躊躇", turn.InitialOutOfBudgetLemmas);

        // Verify final rewritten reply has 0 out-of-budget words, uses "+1" target word "焚き火", and has AngleSharp furigana + .ir-chat-target-plus1 badge
        Assert.Empty(turn.FinalVocabularyAudit.OutOfBudgetLemmas);
        Assert.Contains("焚き火", turn.FinalVocabularyAudit.UsedPlusOneLemmas);
        Assert.Contains("ir-tok", turn.PersonaMessage.EnrichedHtml);
        Assert.Contains("ir-chat-target-plus1", turn.PersonaMessage.EnrichedHtml);
        Assert.Contains("<ruby", turn.PersonaMessage.EnrichedHtml);
    }

    private sealed class MockScriptedLlmProvider : ILlmProvider
    {
        public string ProviderId => "mock-scripted";
        public string DisplayName => "Mock Scripted LLM";
        public bool IsLocalOffline => true;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
            LlmRequest request,
            CancellationToken ct = default)
        {
            string json = request.TaskKind switch
            {
                "quiz.generate.mcq" => """
                    {
                      "questionPrompt": "クークは海辺で何の準備を始めましたか？",
                      "options": [
                        "焚き火の準備を始めた。",
                        "船で釣りに向かった。",
                        "山小屋へ帰った。",
                        "昼寝を始めた。"
                      ],
                      "correctOptionIndex": 0,
                      "evidenceQuote": "クークは海辺で焚き火の準備を始めました。",
                      "paragraphId": 0,
                      "explanation": "本文に「焚き火の準備を始めました」と書かれています。"
                    }
                    """,
                "quiz.generate.freeform" => """
                    {
                      "questionPrompt": "クークが海辺でしたことを説明してください。",
                      "options": [],
                      "correctOptionIndex": 0,
                      "evidenceQuote": "クークは海辺で焚き火の準備を始めました。",
                      "paragraphId": 0,
                      "modelAnswer": "クークは海辺で焚き火の準備を始めました。"
                    }
                    """,
                "quiz.grade.freeform" => """
                    {
                      "comprehensionScore": 92,
                      "evidenceQuote": "クークは海辺で焚き火の準備を始めました。",
                      "missingOrMisunderstood": "主要な行動が正確に説明されています。",
                      "modelAnswer": "クークは海辺で焚き火の準備を始めました。"
                    }
                    """,
                "persona.chat.initial" => """
                    {
                      "reply": "ぼくは漸次、周囲の趨勢を見ながら少し躊躇していたんだ。"
                    }
                    """,
                "persona.chat.rewrite" => """
                    {
                      "reply": "ぼくは少し迷ったけれど、いっしょに焚き火の準備に行くよ！"
                    }
                    """,
                _ => """{"reply":"うん、いっしょに行こう！"}"""
            };

            T val = JsonSerializer.Deserialize<T>(json, JsonOpts)!;
            return Task.FromResult(new LlmStructuredResponse<T>(val, json, "mock-model"));
        }
    }

    private sealed class TestFeaturePlugin(
        string pluginId,
        Action<IFeaturePluginContext>? onInit = null,
        bool throwOnInit = false) : IFeaturePlugin
    {
        public string PluginId => pluginId;
        public string DisplayName => $"Test Plugin {pluginId}";
        public string Version => "1.0.0";

        public ValueTask InitializeAsync(IFeaturePluginContext context, CancellationToken ct = default)
        {
            if (throwOnInit)
            {
                throw new InvalidOperationException("Failed to initialize test plugin");
            }

            onInit?.Invoke(context);
            return ValueTask.CompletedTask;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task FeaturePluginHost_PrefixesRpcAction_WithPluginId()
    {
        using var host = new FeaturePluginHost(_readerContext, _vocabBudget, _snippetEnricher);
        var plugin = new TestFeaturePlugin("samplePlugin", ctx =>
        {
            ctx.RegisterRpcAction("ping", (p, ct) => Task.FromResult<object?>("pong"));
        });

        await host.RegisterPluginAsync(plugin);

        Assert.Single(host.Plugins);
        Assert.True(host.TryGetRpcHandler("ext.samplePlugin.ping", out var handler));
        Assert.NotNull(handler);
        var result = await handler!(default, default);
        Assert.Equal("pong", result);
    }

    [Fact]
    public async Task FeaturePluginHost_RegisterRpcAction_ThrowsOnDuplicateKey()
    {
        using var host = new FeaturePluginHost(_readerContext, _vocabBudget, _snippetEnricher);
        var plugin = new TestFeaturePlugin("dupPlugin", ctx =>
        {
            ctx.RegisterRpcAction("action1", (p, ct) => Task.FromResult<object?>(null));
            ctx.RegisterRpcAction("action1", (p, ct) => Task.FromResult<object?>(null));
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => host.RegisterPluginAsync(plugin).AsTask());
        Assert.Contains("action1", ex.Message);
    }

    [Fact]
    public async Task FeaturePluginHost_FailingPlugin_IsNotAddedToPlugins()
    {
        using var host = new FeaturePluginHost(_readerContext, _vocabBudget, _snippetEnricher);
        var failingPlugin = new TestFeaturePlugin("failingPlugin", throwOnInit: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RegisterPluginAsync(failingPlugin).AsTask());

        Assert.Empty(host.Plugins);
    }
}

