namespace ImmersiveReading.Core.Plugins;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Knowledge;
using ImmersiveReading.Core.Prompts;

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
    /// <summary>Metadata read/write access to the library (books, tags, custom fields).</summary>
    IHostLibrary Library { get; }
    IReaderContextService ReaderContext { get; }
    IVocabularyBudgetService VocabularyBudget { get; }
    ISnippetEnricher SnippetEnricher { get; }
    /// <summary>Ask the knowledge graphs (a book's characters, events and items, what the user told us, later a subject) without knowing which plugin holds them.</summary>
    IKnowledgeHub Knowledge { get; }
    /// <summary>The prompts of every feature; the user can replace any of them with an edited copy (Settings -> Prompts).</summary>
    IPromptCatalog Prompts { get; }

    // Scoped Registrations (Automatically disposed when plugin is disabled)
    IDisposable RegisterRpcAction(string actionName, PluginRpcHandler handler);
    IDisposable RegisterLanguageAnalyzer(ILanguagePlugin languagePlugin, bool isFallback = false);
    IDisposable RegisterDomEnrichmentStage(IDomEnrichmentStage stage);
    IDisposable RegisterPythonStage(PythonStageDescriptor stage);
    /// <summary>Makes a body of knowledge available to every feature through <see cref="Knowledge"/>; removed when the plugin is disabled.</summary>
    IDisposable RegisterKnowledgeSource(IKnowledgeSource source);
    /// <summary>Registers this plugin's built-in prompt templates (full file texts, front matter included); removed when the plugin is disabled.</summary>
    IDisposable RegisterPrompts(IEnumerable<string> templateTexts);

    Stream? OpenAssetStream(string relativeAssetPath);
}
