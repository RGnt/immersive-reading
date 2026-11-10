using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Core.Extensions;

/// <summary>
/// Host services handed to plugin modules when they create their feature plugins.
/// </summary>
public sealed record PluginServices(
    IReaderContextService ReaderContext,
    IVocabularyBudgetService VocabularyBudget,
    ISnippetEnricher SnippetEnricher,
    LanguagePluginRegistry Languages,
    IConfigurationManager Config,
    IServiceProvider Extra);

/// <summary>
/// A deployable unit of extensibility. A module may contribute language plugins,
/// register its own internal dependencies via <see cref="IServiceCollection"/>,
/// and create feature plugins resolved from <see cref="IServiceProvider"/>.
/// </summary>
public interface IPluginModule : IDisposable
{
    string ModuleId { get; }

    /// <summary>Called once, before host services are built. Register language plugins here.</summary>
    void RegisterLanguages(LanguagePluginRegistry languages)
    {
    }

    /// <summary>Called during host service container configuration. Register module-specific services here.</summary>
    void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>Called once, after the host service provider is built. Return the feature plugins to register.</summary>
    IReadOnlyList<IFeaturePlugin> CreateFeaturePlugins(IServiceProvider provider) => [];

    /// <summary>Called once, after the host service provider is built. Return modern unified plugins (IImmersivePlugin).</summary>
    IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider) => [];

    /// <summary>Legacy overload for backwards compatibility.</summary>
    IReadOnlyList<IFeaturePlugin> CreateFeaturePlugins(PluginServices services) => [];
}
