using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Database;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using ImmersiveReading.Plugins.AiCompanion.Schema;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Plugins.AiCompanion;

public sealed class AiCompanionPlugin : IFeaturePlugin, IImmersivePlugin
{
    public const string PluginIdValue = "aiCompanion";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdValue,
        Name = "AI Reading Companion",
        Version = "2.0.0",
        Author = "RGnt",
        Description = "Zero-spoiler persona chat and reading comprehension quizzes.",
        ReloadBehavior = PluginReloadBehavior.Hot,
        UiEntry = "ui.js"
    };

    public string PluginId => PluginIdValue;
    public string DisplayName => "AI Reading Comprehension & n+1 Persona Companion";
    public string Version => "2.0.0";

    private readonly IDbConnectionFactory _db;
    public ComprehensionQuizService QuizService { get; }
    public PersonaChatService PersonaChatService { get; }
    public ILlmProvider LlmProvider { get; }

    public AiCompanionPlugin(
        IDbConnectionFactory db,
        IReaderContextService readerContext,
        IVocabularyBudgetService vocabBudget,
        ISnippetEnricher snippetEnricher,
        ILanguagePluginResolver languages,
        ILlmProvider? llmProvider = null)
    {
        _db = db;
        LlmProvider = llmProvider ?? new OpenAiCompatibleLlmProvider();
        QuizService = new ComprehensionQuizService(db, readerContext, snippetEnricher, LlmProvider);
        PersonaChatService = new PersonaChatService(db, readerContext, vocabBudget, snippetEnricher, languages, LlmProvider);
    }

    public async ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        await context.Database.MigrateFromEmbeddedResourcesAsync(
            typeof(AiCompanionPlugin).Assembly,
            "ImmersiveReading.Plugins.AiCompanion.Migrations",
            ct);

        RegisterSharedRpcActions(
            (action, handler) => context.RegisterRpcAction(action, handler),
            context.Database);
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask InitializeAsync(IFeaturePluginContext context, CancellationToken ct = default)
    {
        using (var conn = _db.OpenConnection())
        {
            MigrationRunner.ApplyMigrations(conn, AiCompanionMigrations.ModuleId, AiCompanionMigrations.All);
        }

        context.RegisterUiPanel(new ReaderUiPanelDescriptor(
            PanelId: "ai-companion-drawer",
            Title: "AI Comprehension & n+1 Persona Chat",
            Icon: "🧠",
            Location: "reader-drawer"));

        RegisterSharedRpcActions((action, handler) => context.RegisterRpcAction(action, (p, tok) => handler(p, tok)), dbContext: null);
        return ValueTask.CompletedTask;
    }

    private void RegisterSharedRpcActions(
        Action<string, PluginRpcHandler> register,
        IPluginDatabaseContext? dbContext)
    {
        register("getStage", (p, _) =>
        {
            string bookId = p.TryGetProperty("bookId", out var b) ? (b.GetString() ?? "") : "";
            var stage = QuizService.GetCurrentStage(bookId);
            return Task.FromResult<object?>(new { bookId, stage = stage.ToString() });
        });

        register("generateQuestion", async (p, token) =>
        {
            return await HandleGenerateQuestion(p, token);
        });

        register("generateQuiz", async (p, token) =>
        {
            return await HandleGenerateQuestion(p, token);
        });

        register("submitAnswer", async (p, token) =>
        {
            return await HandleSubmitAnswer(p, token);
        });

        register("submitQuizAnswer", async (p, token) =>
        {
            return await HandleSubmitAnswer(p, token);
        });

        register("listPersonas", (p, _) =>
        {
            string? bookId = p.TryGetProperty("bookId", out var bid) && bid.ValueKind == JsonValueKind.String ? bid.GetString() : null;
            string? bookTitle = p.TryGetProperty("bookTitle", out var bt) && bt.ValueKind == JsonValueKind.String ? bt.GetString() : null;
            var personas = PersonaChatService.ListPersonas(bookId, bookTitle);
            return Task.FromResult<object?>(new { personas });
        });

        register("createPersona", (p, _) =>
        {
            string? bookId = p.TryGetProperty("bookId", out var bid) && bid.ValueKind == JsonValueKind.String ? bid.GetString() : null;
            string name = p.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            string roleSummary = p.TryGetProperty("roleSummary", out var rs) && rs.ValueKind == JsonValueKind.String ? (rs.GetString() ?? "") : "";
            string speechStyle = p.TryGetProperty("speechStyle", out var ss) && ss.ValueKind == JsonValueKind.String ? (ss.GetString() ?? "") : "";
            var samples = new List<string>();
            if (p.TryGetProperty("voiceSamples", out var vs) && vs.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in vs.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        samples.Add(item.GetString()!);
                    }
                }
            }
            var created = PersonaChatService.CreatePersona(bookId, name, roleSummary, speechStyle, samples);
            return Task.FromResult<object?>(created);
        });

        register("sendChatMessage", async (p, token) =>
        {
            string personaId = p.TryGetProperty("personaId", out var pid) ? (pid.GetString() ?? "") : "";
            string message = p.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "";
            int? chapterIdx = p.TryGetProperty("chapterIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
            string? spineHref = p.TryGetProperty("spineHref", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
            int? viewportTok = p.TryGetProperty("viewportTokenId", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : null;

            return await PersonaChatService.SendMessageAsync(
                personaId,
                message,
                chapterTargetWords: null,
                overrideChapterIndex: chapterIdx,
                overrideSpineHref: spineHref,
                overrideViewportTokenId: viewportTok,
                ct: token).ConfigureAwait(false);
        });

        register("sendMessage", async (p, token) =>
        {
            string personaId = p.TryGetProperty("personaId", out var pid) && pid.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(pid.GetString())
                ? pid.GetString()!
                : "default";

            string message = p.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "";
            int? chapterIdx = p.TryGetProperty("chapterIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
            string? spineHref = p.TryGetProperty("spineHref", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
            int? viewportTok = p.TryGetProperty("viewportTokenId", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : null;
            string bookId = p.TryGetProperty("bookId", out var b) ? (b.GetString() ?? "") : "";

            var reply = await PersonaChatService.SendMessageAsync(
                personaId,
                message,
                chapterTargetWords: null,
                overrideChapterIndex: chapterIdx,
                overrideSpineHref: spineHref,
                overrideViewportTokenId: viewportTok,
                ct: token).ConfigureAwait(false);

            if (dbContext != null && !string.IsNullOrEmpty(bookId))
            {
                try
                {
                    await using var conn = (SqliteConnection)dbContext.CreateConnection();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO ai_chat_messages (message_id, book_id, persona_name, role, raw_text, enriched_html)
                        VALUES ($mid1, $bid, $pname, 'user', $text1, $html1),
                               ($mid2, $bid, $pname, 'assistant', $text2, $html2);
                        """;
                    cmd.Parameters.AddWithValue("$mid1", Guid.NewGuid().ToString("N"));
                    cmd.Parameters.AddWithValue("$bid", bookId);
                    cmd.Parameters.AddWithValue("$pname", personaId);
                    cmd.Parameters.AddWithValue("$text1", message);
                    cmd.Parameters.AddWithValue("$html1", reply.UserMessage.EnrichedHtml);
                    cmd.Parameters.AddWithValue("$mid2", Guid.NewGuid().ToString("N"));
                    cmd.Parameters.AddWithValue("$text2", reply.PersonaMessage.RawText);
                    cmd.Parameters.AddWithValue("$html2", reply.PersonaMessage.EnrichedHtml);
                    await cmd.ExecuteNonQueryAsync(token);
                }
                catch { /* Ignore storage error */ }
            }

            return new
            {
                userHtml = reply.UserMessage.EnrichedHtml,
                assistantHtml = reply.PersonaMessage.EnrichedHtml,
                replyText = reply.PersonaMessage.RawText
            };
        });

        register("getHistory", async (p, token) =>
        {
            string bookId = p.TryGetProperty("bookId", out var bid) ? (bid.GetString() ?? "") : "";
            var messages = new List<object>();

            if (dbContext != null && !string.IsNullOrEmpty(bookId))
            {
                try
                {
                    await using var conn = (SqliteConnection)dbContext.CreateConnection();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT role, enriched_html FROM ai_chat_messages WHERE book_id = $bid ORDER BY created_at ASC;";
                    cmd.Parameters.AddWithValue("$bid", bookId);
                    await using var reader = await cmd.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token))
                    {
                        messages.Add(new
                        {
                            role = reader.GetString(0),
                            enrichedHtml = reader.GetString(1)
                        });
                    }
                }
                catch { /* Ignore read error */ }
            }

            return new { messages };
        });

        register("testEndpoint", async (p, token) =>
        {
            string ep = p.TryGetProperty("endpointUrl", out var e) ? (e.GetString() ?? "") : "http://127.0.0.1:8080/v1";
            string key = p.TryGetProperty("apiKey", out var k) ? (k.GetString() ?? "") : "";
            string model = p.TryGetProperty("modelId", out var m) ? (m.GetString() ?? "") : "qwen3-8b";
            var res = await LlmEndpointTester.TestAsync(ep, key, model, 10, token);
            return new { success = res.Success, latencyMs = res.LatencyMs, message = res.StatusMessage, models = res.Models };
        });
    }

    private async Task<object?> HandleGenerateQuestion(JsonElement p, CancellationToken token)
    {
        string bookId = p.TryGetProperty("bookId", out var b) ? (b.GetString() ?? "") : "";
        ComprehensionStage? overrideStage = null;
        if (p.TryGetProperty("stage", out var stProp) && stProp.ValueKind == JsonValueKind.String &&
            Enum.TryParse<ComprehensionStage>(stProp.GetString(), ignoreCase: true, out var parsedStage))
        {
            overrideStage = parsedStage;
        }

        int? checkpointTok = p.TryGetProperty("checkpointTokenId", out var ck) && ck.ValueKind == JsonValueKind.Number ? ck.GetInt32() : null;
        int? viewportTok = p.TryGetProperty("viewportTokenId", out var vp) && vp.ValueKind == JsonValueKind.Number ? vp.GetInt32() : null;
        int? chapterIdx = p.TryGetProperty("chapterIndex", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
        string? spineHref = p.TryGetProperty("spineHref", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;

        var question = await QuizService.GenerateQuestionAsync(
            bookId,
            overrideStage,
            checkpointTok,
            viewportTok,
            chapterIdx,
            spineHref,
            token).ConfigureAwait(false);

        return new
        {
            questionId = question.QuestionId,
            id = question.QuestionId,
            stage = question.Stage.ToString(),
            questionMode = question.QuestionMode,
            questionText = question.PromptText,
            promptSyntheticChapterHref = question.PromptSyntheticChapterHref,
            options = question.Options.Select(o => o.Text).ToArray(),
            chapterIndex = question.ChapterIndex,
            startTokenId = question.StartTokenId,
            endTokenId = question.EndTokenId,
            sourceParagraphId = question.SourceParagraphId,
            evidenceQuote = question.EvidenceQuote,
            questions = new[]
            {
                new
                {
                    id = question.QuestionId,
                    promptEnrichedHtml = question.PromptHtml,
                    options = question.Options.Select(o => o.EnrichedHtml).ToArray()
                }
            },
            question
        };
    }

    private async Task<object?> HandleSubmitAnswer(JsonElement p, CancellationToken token)
    {
        string questionId = p.TryGetProperty("questionId", out var qid) ? (qid.GetString() ?? "") : "";
        string userAnswer = p.TryGetProperty("userAnswer", out var ua) && ua.ValueKind == JsonValueKind.String ? (ua.GetString() ?? "") : "";
        int? selectedIdx = p.TryGetProperty("selectedOptionIndex", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : null;
        var res = await QuizService.SubmitAnswerAsync(questionId, userAnswer, selectedIdx, token).ConfigureAwait(false);
        return new
        {
            questionId = res.QuestionId,
            comprehensionScore = res.ComprehensionScore,
            isCorrect = res.IsCorrect,
            modelAnswer = res.ModelAnswerHtml,
            critiqueExplanation = res.MissingOrMisunderstood,
            nextRecommendedStage = 2,
            result = res
        };
    }

    public void Dispose()
    {
        if (LlmProvider is IDisposable d)
        {
            d.Dispose();
        }
    }
}
