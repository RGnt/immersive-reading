using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Plugins.AiCompanion;

/// <summary>
/// Plugin module for the AI Companion. Owns the LLM provider wiring via <see cref="ILlmProviderFactory"/>
/// and registers its services into the IoC container via <see cref="ConfigureServices"/>.
/// </summary>
public sealed class AiCompanionModule : IPluginModule
{
    private readonly ILlmProviderFactory _providerFactory;
    private IConfigurationManager? _config;
    private OpenAiCompatibleLlmProvider? _provider;
    private EventHandler<AppConfig>? _configChangedHandler;

    public string ModuleId => AiCompanionPlugin.PluginIdValue;

    public AiCompanionModule(ILlmProviderFactory? providerFactory = null)
    {
        _providerFactory = providerFactory ?? DefaultLlmProviderFactory.Instance;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(_providerFactory);
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfigurationManager>();
            var factory = sp.GetRequiredService<ILlmProviderFactory>();
            var provider = factory.CreateProvider(config.CurrentConfig.Llm);

            if (provider is OpenAiCompatibleLlmProvider openAiProvider)
            {
                _provider = openAiProvider;
                _config = config;
                _configChangedHandler = (_, cfg) => openAiProvider.UpdateOptions(ToEndpointOptions(cfg.Llm));
                config.ConfigChanged += _configChangedHandler;
            }

            return provider;
        });

        services.AddSingleton(sp => new AiCompanionPlugin(
            sp.GetRequiredService<IDbConnectionFactory>(),
            sp.GetRequiredService<IReaderContextService>(),
            sp.GetRequiredService<IVocabularyBudgetService>(),
            sp.GetRequiredService<ISnippetEnricher>(),
            sp.GetRequiredService<ILanguagePluginResolver>(),
            sp.GetRequiredService<ILlmProvider>()));
    }

    public IReadOnlyList<IFeaturePlugin> CreateFeaturePlugins(IServiceProvider provider)
    {
        var plugin = provider.GetRequiredService<AiCompanionPlugin>();
        return [plugin];
    }

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
    {
        var plugin = provider.GetRequiredService<AiCompanionPlugin>();
        return [plugin];
    }

    public IReadOnlyList<IFeaturePlugin> CreateFeaturePlugins(PluginServices services)
    {
        var db = (services.Extra.GetService(typeof(IDbConnectionFactory)) as IDbConnectionFactory)
            ?? (services.Extra.GetService(typeof(object)) as IDbConnectionFactory)
            ?? throw new InvalidOperationException(
                $"{nameof(AiCompanionModule)} requires {nameof(IDbConnectionFactory)} in {nameof(PluginServices)}.{nameof(PluginServices.Extra)}.");

        _config = services.Config;
        var provider = _providerFactory.CreateProvider(_config.CurrentConfig.Llm);
        _provider = provider as OpenAiCompatibleLlmProvider;

        if (_provider is not null)
        {
            _configChangedHandler = (_, cfg) => _provider.UpdateOptions(ToEndpointOptions(cfg.Llm));
            _config.ConfigChanged += _configChangedHandler;
        }

        var plugin = new AiCompanionPlugin(
            db,
            services.ReaderContext,
            services.VocabularyBudget,
            services.SnippetEnricher,
            services.Languages,
            provider);

        return [plugin];
    }

    internal static LlmEndpointOptions ToEndpointOptions(LlmSettings llm) => new(
        EndpointUrl: string.IsNullOrWhiteSpace(llm.EndpointUrl) ? "offline://heuristic" : llm.EndpointUrl,
        ModelId: llm.ModelId,
        ApiKey: llm.ApiKey,
        TimeoutSeconds: llm.TimeoutSeconds,
        EnableOfflineHeuristicFallback: llm.EnableOfflineHeuristicFallback);

    public void Dispose()
    {
        if (_config is not null && _configChangedHandler is not null)
        {
            _config.ConfigChanged -= _configChangedHandler;
        }

        _configChangedHandler = null;
    }
}
