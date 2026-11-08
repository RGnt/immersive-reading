namespace ImmersiveReading.Core.Plugins;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;

public delegate Task<object?> PluginRpcHandler(JsonElement payload, CancellationToken ct);

public interface IPluginHostContext
{
    PluginManifest Manifest { get; }
    string PluginDataDirectory { get; }

    IPluginDatabaseContext Database { get; }
    IPluginConfigContext Config { get; }
    IPluginEventBus EventBus { get; }

    // Controlled Core Host Services
    IBookCatalog BookCatalog { get; }
    IReaderContextService ReaderContext { get; }
    IVocabularyBudgetService VocabularyBudget { get; }
    ISnippetEnricher SnippetEnricher { get; }

    // Scoped Registrations (Automatically disposed when plugin is disabled)
    IDisposable RegisterRpcAction(string actionName, PluginRpcHandler handler);
    IDisposable RegisterLanguageAnalyzer(ILanguagePlugin languagePlugin, bool isFallback = false);
    IDisposable RegisterDomEnrichmentStage(IDomEnrichmentStage stage);
    IDisposable RegisterPythonStage(PythonStageDescriptor stage);

    Stream? OpenAssetStream(string relativeAssetPath);
}
