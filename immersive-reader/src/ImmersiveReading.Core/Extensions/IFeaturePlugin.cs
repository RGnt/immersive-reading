using System.Text.Json;

namespace ImmersiveReading.Core.Extensions;

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

