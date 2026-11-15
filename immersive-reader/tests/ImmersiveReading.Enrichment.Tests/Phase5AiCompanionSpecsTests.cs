using System.Text.Json;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Library;
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

    private sealed class FakeCatalog(string bookId, IReadOnlyList<BookChapterRecord> chapters) : ImmersiveReading.Core.Abstractions.IBookCatalog
    {
        public BookSummary? GetBookSummary(string id) => id == bookId
            ? new BookSummary(id, "sha", null, null, "クークの本", "クークの本", "", null, 0, "", "", "", "", "", "", "ja", "", false, "", "", 0, null,
                chapters.Count, 0, true, "horizontal-tb", "", "", null, false, 0, [], new Dictionary<string, EffectiveCustomFieldValue>())
            : null;
        public BookDetail? GetBookDetail(string id) => GetBookSummary(id) is { } s ? new BookDetail(s, new OpfOriginalMetadata(), chapters, [], new Dictionary<string, string>()) : null;
        public string? GetEpubFilePath(string id) => null;
    }

    private (ReaderContextService Reader, string BookId, string Ch1Text) CompletedChapterBook()
    {
        string bookId = "book-quiz-gate";
        // chapter 0 is excluded front matter, chapter 1 is the last COMPLETED chapter, chapter 2 is the one being read
        var chapters = new List<BookChapterRecord>
        {
            new(bookId, 0, "c0", "表紙", "xhtml/cover.xhtml", IsExcluded: true),
            new(bookId, 1, "c1", "第一章", "xhtml/ch1.xhtml"),
            new(bookId, 2, "c2", "第二章", "xhtml/ch2.xhtml")
        };
        string ch1Html = "<html><body><p>クークは海辺で焚き火の準備を始めました。</p><p>ミミは流木を集めて、テントのそばに積みました。</p></body></html>";
        string ch2Html = "<html><body><p>未来のネタバレ：ミミは宝箱を見つけました。</p></body></html>";
        _cache.Set(AngleSharpDomEnricher.EnrichChapter(ch1Html, bookId, "xhtml/ch1.xhtml", _jaPlugin, new EnrichmentOptions(FuriganaMode.AllKanji)).TokenMap);
        _cache.Set(AngleSharpDomEnricher.EnrichChapter(ch2Html, bookId, "xhtml/ch2.xhtml", _jaPlugin, new EnrichmentOptions(FuriganaMode.AllKanji)).TokenMap);
        return (new ReaderContextService(new FakeCatalog(bookId, chapters), _cache, _db), bookId, "クークは海辺で焚き火の準備を始めました。");
    }

    [Fact]
    public async Task Scenario2_And_Scenario3_Quiz_AsksAboutTheLastCompletedChapterOnly_McqToFreeFormProgression_And_RubricOrder()
    {
        var (reader, bookId, evidence) = CompletedChapterBook();
        var mockLlm = new MockScriptedLlmProvider();
        var quizService = new ComprehensionQuizService(_db, reader, _snippetEnricher, mockLlm) { Rng = new Random(7) };

        // Stage 1: initial stage is multiple choice
        Assert.Equal(ComprehensionStage.MultipleChoice, quizService.GetCurrentStage(bookId));

        // the reader is in chapter 2: the question is about chapter 1 and nothing of chapter 2 reaches the model
        var mcqQuestion = await quizService.GenerateQuestionAsync(bookId, overrideChapterIndex: 2);

        Assert.Equal(1, mcqQuestion.ChapterIndex);
        Assert.Equal(ComprehensionStage.MultipleChoice, mcqQuestion.Stage);
        Assert.Equal(4, mcqQuestion.Options.Count);
        Assert.Contains("ir-tok", mcqQuestion.PromptHtml);
        Assert.All(mcqQuestion.Options, o => Assert.Contains("ir-tok", o.EnrichedHtml));
        Assert.All(mockLlm.Requests, r => Assert.DoesNotContain("ネタバレ", string.Join(" ", r.Messages.Select(m => m.Content))));
        Assert.Contains("[0] クークは海辺", mockLlm.Requests[0].Messages[1].Content);        // the passage reaches the model with numbered paragraphs

        var wrongEval = await quizService.SubmitAnswerAsync((await quizService.GenerateQuestionAsync(bookId, overrideChapterIndex: 2)).QuestionId, "9", selectedOptionIndex: 9);
        Assert.False(wrongEval.IsCorrect);
        int right = mcqQuestion.Options.ToList().FindIndex(o => o.Text == "焚き火の準備を始めた。");
        var mcqEval = await quizService.SubmitAnswerAsync(mcqQuestion.QuestionId, right.ToString(), selectedOptionIndex: right);
        Assert.True(mcqEval.IsCorrect);
        Assert.Equal(100, mcqEval.ComprehensionScore);
        Assert.Contains("焚き火", mcqEval.EvidenceQuote);

        // Scenario 3: four more answers averaging 90% put the learner on free-form questions
        for (int i = 0; i < 4; i++) quizService.RecordHistoricalScoreForTesting(bookId, ComprehensionStage.MultipleChoice, 0.90);
        Assert.Equal(ComprehensionStage.FreeForm, quizService.GetCurrentStage(bookId));

        var freeFormQuestion = await quizService.GenerateQuestionAsync(bookId, overrideChapterIndex: 2);
        Assert.Equal("freeform", freeFormQuestion.QuestionMode);
        Assert.Empty(freeFormQuestion.Options);

        var ffEval = await quizService.SubmitAnswerAsync(freeFormQuestion.QuestionId, "クークは海辺で焚き火の準備をしていました。");

        // the rubric is judged in order: comprehension first (it drives the stage), then grammar, then vocabulary
        Assert.Equal(92, ffEval.ComprehensionScore);
        var rubric = ffEval.Rubric!;
        Assert.True(rubric.Counted);
        Assert.Equal((92, 80, 70), (rubric.Comprehension, rubric.Grammar, rubric.Vocabulary));
        Assert.Equal(86, rubric.Overall);                                                       // 0.60 x 92 + 0.25 x 80 + 0.15 x 70
        Assert.Equal(["準備をしていました"], rubric.GrammarIssues.Select(i => i.Original).ToArray());   // the invented issue is not in the answer: dropped
        Assert.Equal(["焚き火"], rubric.WellUsed.Select(w => w.Lemma).ToArray());
        Assert.Contains("ir-tok", ffEval.ModelAnswerHtml);
    }

    [Fact]
    public async Task Quiz_IsUnavailableUntilAChapterIsCompleted_AndRefusesAQuestionTheModelCannotSupport()
    {
        var (reader, bookId, _) = CompletedChapterBook();
        var llm = new MockScriptedLlmProvider();
        var quiz = new ComprehensionQuizService(_db, reader, _snippetEnricher, llm) { Rng = new Random(1) };

        // reading chapter 1 (the first real chapter): nothing is completed yet, and chapter 1 itself must not be used
        await Assert.ThrowsAsync<QuizUnavailableException>(() => quiz.GenerateQuestionAsync(bookId, overrideChapterIndex: 1));
        Assert.Empty(llm.Requests);

        // a model that keeps inventing evidence gets one more try with the reason, then the quiz says so: no canned question
        llm.QuestionJson = """{"questionPrompt":"誰が来ましたか？","options":["A先生","Bさん","C君","D君"],"correctOptionIndex":0,"evidenceQuote":"この文は本文にありません。"}""";
        var ex = await Assert.ThrowsAsync<QuizGenerationException>(() => quiz.GenerateQuestionAsync(bookId, overrideChapterIndex: 2));
        Assert.Contains("not found in the passage", ex.Message);
        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("YOUR PREVIOUS QUESTION WAS REJECTED", llm.Requests[1].Messages[1].Content);
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
        public List<LlmRequest> Requests { get; } = [];
        public string? QuestionJson { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        public Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
            LlmRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            string json = QuestionJson is not null && request.TaskKind?.StartsWith("quiz.generate") == true ? QuestionJson : request.TaskKind switch
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
                      "comprehension": { "score": 92, "evidenceQuote": "クークは海辺で焚き火の準備を始めました。", "missing": "主要な行動が正確に説明されています。" },
                      "grammar": { "score": 80, "issues": [
                        { "original": "準備をしていました", "correction": "準備を始めました", "note": "本文は「始めました」です。" },
                        { "original": "この文は答えにありません", "correction": "x", "note": "invented" } ] },
                      "vocabulary": { "score": 70, "wellUsed": [ { "surface": "焚き火", "lemma": "焚き火" } ], "misused": [] },
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

