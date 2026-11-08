using System.Text.Json;

namespace ImmersiveReading.Core.Extensions;

public sealed class FeaturePluginHost : IFeaturePluginContext, IDisposable
{
    private readonly object _lock = new();
    private readonly List<IFeaturePlugin> _plugins = [];
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _rpcActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReaderUiPanelDescriptor> _uiPanels = [];
    private readonly List<IDomEnrichmentStage> _domStages = [];

    public IReaderContextService ReaderContext { get; }
    public IVocabularyBudgetService VocabularyBudget { get; }
    public ISnippetEnricher SnippetEnricher { get; }

    public IReadOnlyList<IFeaturePlugin> Plugins
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    public IReadOnlyList<ReaderUiPanelDescriptor> UiPanels
    {
        get { lock (_lock) return _uiPanels.ToList(); }
    }

    public IReadOnlyList<IDomEnrichmentStage> DomStages
    {
        get { lock (_lock) return _domStages.ToList(); }
    }

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
        var scope = new PluginContextScope(this, plugin.PluginId);
        await plugin.InitializeAsync(scope, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _plugins.Add(plugin);
        }
    }

    internal void AddRpcAction(string key, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        lock (_lock)
        {
            if (_rpcActions.ContainsKey(key))
            {
                throw new InvalidOperationException($"RPC action '{key}' is already registered.");
            }
            _rpcActions[key] = handler;
        }
    }

    internal void AddUiPanel(ReaderUiPanelDescriptor panel)
    {
        lock (_lock)
        {
            _uiPanels.Add(panel);
        }
    }

    internal void AddDomStage(IDomEnrichmentStage stage)
    {
        lock (_lock)
        {
            _domStages.Add(stage);
        }
    }

    void IFeaturePluginContext.RegisterRpcAction(string action, Func<JsonElement, CancellationToken, Task<object?>> handler)
    {
        string key = action.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
            ? action
            : $"ext.global.{action}";
        AddRpcAction(key, handler);
    }

    void IFeaturePluginContext.RegisterUiPanel(ReaderUiPanelDescriptor panel) => AddUiPanel(panel);

    void IFeaturePluginContext.RegisterDomEnrichmentStage(IDomEnrichmentStage stage) => AddDomStage(stage);

    public bool TryGetRpcHandler(string method, out Func<JsonElement, CancellationToken, Task<object?>>? handler)
    {
        lock (_lock)
        {
            return _rpcActions.TryGetValue(method, out handler);
        }
    }

    public void Dispose()
    {
        List<IFeaturePlugin> pluginsToDispose;
        lock (_lock)
        {
            pluginsToDispose = [.. _plugins];
            _plugins.Clear();
            _rpcActions.Clear();
            _uiPanels.Clear();
            _domStages.Clear();
        }

        foreach (var plugin in pluginsToDispose)
        {
            plugin.Dispose();
        }
    }

    private sealed class PluginContextScope(FeaturePluginHost host, string pluginId) : IFeaturePluginContext
    {
        public IReaderContextService ReaderContext => host.ReaderContext;
        public IVocabularyBudgetService VocabularyBudget => host.VocabularyBudget;
        public ISnippetEnricher SnippetEnricher => host.SnippetEnricher;

        public void RegisterRpcAction(string action, Func<JsonElement, CancellationToken, Task<object?>> handler)
        {
            string key = action.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
                ? action
                : $"ext.{pluginId}.{action}";
            host.AddRpcAction(key, handler);
        }

        public void RegisterUiPanel(ReaderUiPanelDescriptor panel) => host.AddUiPanel(panel);

        public void RegisterDomEnrichmentStage(IDomEnrichmentStage stage) => host.AddDomStage(stage);
    }
}

