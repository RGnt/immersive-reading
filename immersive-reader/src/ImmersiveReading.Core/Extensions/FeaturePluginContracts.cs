using System.Text.Json;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Core.Extensions;

public sealed record ReaderUiPanelDescriptor(
    string PanelId,
    string Title,
    string Icon,
    string Location = "reader-drawer"
);

public interface IDomEnrichmentStage
{
    string StageId { get; }
    int Order { get; }
}

public sealed record ParagraphSlice(
    int ParagraphId,
    int StartTokenId,
    int EndTokenId,
    string Text
);

public sealed record ReadPassageSlice(
    string BookId,
    string BookTitle,
    int ChapterIndex,
    string ChapterTitle,
    string SpineHref,
    int StartTokenId,
    int EndTokenId,
    string PlainText,
    IReadOnlyList<ParagraphSlice> Paragraphs
);

public interface IReaderContextService
{
    Task<ReadPassageSlice> GetJustReadPassageAsync(
        string bookId,
        int maxCharacters = 3500,
        int? previousCheckpointTokenId = null,
        int? overrideViewportTokenId = null,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReadPassageSlice>> GetStoryContextUpToProgressAsync(
        string bookId,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        int? overrideViewportTokenId = null,
        CancellationToken ct = default);
}

public sealed record EnrichedSnippetResult(
    string SnippetId,
    string SyntheticChapterHref,
    string EnrichedHtml,
    int TokenCount,
    IReadOnlyList<string> HighlightedPlusOneLemmas
);

public interface ISnippetEnricher
{
    EnrichedSnippetResult EnrichSnippet(
        string bookId,
        string htmlOrTextSnippet,
        string languageCode = "ja",
        IReadOnlySet<string>? targetPlusOneLemmas = null);
}

public sealed record VocabularyAuditResult(
    IReadOnlyList<string> UsedPlusOneLemmas,
    IReadOnlyList<string> OutOfBudgetLemmas,
    int TotalContentWords,
    int KnownOrTargetContentWords,
    double KnownRatio
);

public sealed record NPlusOneVocabularyProfile(
    string Language,
    string? BookId,
    IReadOnlySet<string> KnownLemmas,
    IReadOnlySet<string> TargetPlusOneLemmas,
    IReadOnlyList<string> SelectedTurnTargets
);

public interface IVocabularyBudgetService
{
    NPlusOneVocabularyProfile BuildNPlusOneProfile(
        string language = "ja",
        string? bookId = null,
        IReadOnlyCollection<string>? chapterTargetWords = null,
        int maxTurnTargets = 5);

    VocabularyAuditResult AuditReplyTokens(
        IReadOnlyList<AnalyzedToken> tokens,
        NPlusOneVocabularyProfile profile);
}

public sealed record LlmMessage(
    string Role,
    string Content
);

public sealed record LlmRequest(
    IReadOnlyList<LlmMessage> Messages,
    double Temperature = 0.3,
    int MaxTokens = 1024,
    string? TaskKind = null
);

public sealed record LlmStructuredResponse<T>(
    T Value,
    string RawJson,
    string ModelId
);

public interface ILlmProvider
{
    string ProviderId { get; }
    string DisplayName { get; }
    bool IsLocalOffline { get; }

    Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
        LlmRequest request,
        CancellationToken ct = default);
}

public interface IFeaturePluginContext
{
    IReaderContextService ReaderContext { get; }
    IVocabularyBudgetService VocabularyBudget { get; }
    ISnippetEnricher SnippetEnricher { get; }

    void RegisterRpcAction(string action, Func<JsonElement, CancellationToken, Task<object?>> handler);
    void RegisterUiPanel(ReaderUiPanelDescriptor panel);
    void RegisterDomEnrichmentStage(IDomEnrichmentStage stage);
}

public interface IFeaturePlugin : IDisposable
{
    string PluginId { get; }
    string DisplayName { get; }
    string Version { get; }

    ValueTask InitializeAsync(IFeaturePluginContext context, CancellationToken ct = default);
}

public sealed class FeaturePluginHost : IFeaturePluginContext, IDisposable
{
    private readonly List<IFeaturePlugin> _plugins = [];
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _rpcActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReaderUiPanelDescriptor> _uiPanels = [];
    private readonly List<IDomEnrichmentStage> _domStages = [];
    private string _currentInitializingPluginId = "";

    public IReaderContextService ReaderContext { get; }
    public IVocabularyBudgetService VocabularyBudget { get; }
    public ISnippetEnricher SnippetEnricher { get; }

    public IReadOnlyList<IFeaturePlugin> Plugins => _plugins;
    public IReadOnlyList<ReaderUiPanelDescriptor> UiPanels => _uiPanels;
    public IReadOnlyList<IDomEnrichmentStage> DomStages => _domStages;

    public FeaturePluginHost(
        IReaderContextService readerContext,
        IVocabularyBudgetService vocabularyBudget,
        ISnippetEnricher snippetEnricher)
    {
        ReaderContext = readerContext;
        VocabularyBudget = vocabularyBudget;
        SnippetEnricher = snippetEnricher;
    }

    public async ValueTask RegisterPluginAsync(IFeaturePlugin plugin, CancellationToken ct = default)
    {
        _currentInitializingPluginId = plugin.PluginId;
        await plugin.InitializeAsync(this, ct).ConfigureAwait(false);
        _plugins.Add(plugin);
    }

    public void RegisterRpcAction(string action, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        string key = action.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
            ? action
            : $"ext.{_currentInitializingPluginId}.{action}";
        _rpcActions[key] = handler;
    }

    public void RegisterUiPanel(ReaderUiPanelDescriptor panel) => _uiPanels.Add(panel);

    public void RegisterDomEnrichmentStage(IDomEnrichmentStage stage) => _domStages.Add(stage);

    public bool TryGetRpcHandler(string method, out Func<JsonElement, CancellationToken, Task<object?>>? handler)
        => _rpcActions.TryGetValue(method, out handler);

    public void Dispose()
    {
        foreach (var plugin in _plugins)
        {
            plugin.Dispose();
        }
    }
}

