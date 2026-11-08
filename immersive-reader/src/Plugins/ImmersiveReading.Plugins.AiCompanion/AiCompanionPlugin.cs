using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Database;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using ImmersiveReading.Plugins.AiCompanion.Schema;

namespace ImmersiveReading.Plugins.AiCompanion;

public sealed class AiCompanionPlugin : IFeaturePlugin
{
    public const string PluginIdValue = "aiCompanion";

    public string PluginId => PluginIdValue;
    public string DisplayName => "AI Reading Comprehension & n+1 Persona Companion";
    public string Version => "1.0.0";

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

        context.RegisterRpcAction("getStage", (p, _) =>
        {
            string bookId = p.GetProperty("bookId").GetString() ?? "";
            var stage = QuizService.GetCurrentStage(bookId);
            return Task.FromResult<object?>(new { bookId, stage = stage.ToString() });
        });

        context.RegisterRpcAction("generateQuestion", async (p, token) =>
        {
            string bookId = p.GetProperty("bookId").GetString() ?? "";
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

            return await QuizService.GenerateQuestionAsync(
                bookId,
                overrideStage,
                checkpointTok,
                viewportTok,
                chapterIdx,
                spineHref,
                token).ConfigureAwait(false);
        });

        context.RegisterRpcAction("submitAnswer", async (p, token) =>
        {
            string questionId = p.GetProperty("questionId").GetString() ?? "";
            string userAnswer = p.TryGetProperty("userAnswer", out var ua) && ua.ValueKind == JsonValueKind.String ? (ua.GetString() ?? "") : "";
            int? selectedIdx = p.TryGetProperty("selectedOptionIndex", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : null;
            return await QuizService.SubmitAnswerAsync(questionId, userAnswer, selectedIdx, token).ConfigureAwait(false);
        });

        context.RegisterRpcAction("listPersonas", (p, _) =>
        {
            string? bookId = p.TryGetProperty("bookId", out var bid) && bid.ValueKind == JsonValueKind.String ? bid.GetString() : null;
            string? bookTitle = p.TryGetProperty("bookTitle", out var bt) && bt.ValueKind == JsonValueKind.String ? bt.GetString() : null;
            var personas = PersonaChatService.ListPersonas(bookId, bookTitle);
            return Task.FromResult<object?>(new { personas });
        });

        context.RegisterRpcAction("createPersona", (p, _) =>
        {
            string? bookId = p.TryGetProperty("bookId", out var bid) && bid.ValueKind == JsonValueKind.String ? bid.GetString() : null;
            string name = p.GetProperty("name").GetString() ?? "";
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

        context.RegisterRpcAction("sendChatMessage", async (p, token) =>
        {
            string personaId = p.GetProperty("personaId").GetString() ?? "";
            string message = p.GetProperty("message").GetString() ?? "";
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

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        if (LlmProvider is IDisposable d)
        {
            d.Dispose();
        }
    }
}

