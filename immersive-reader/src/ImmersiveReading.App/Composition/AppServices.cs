using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.App.Composition;

/// <summary>
/// Root IoC composition container holding long-lived application services, databases, caches, and plugins.
/// Uses <see cref="IServiceProvider"/> to manage dependency lifetimes and clean disposal.
/// </summary>
public sealed class AppServices : IDisposable
{
    public IServiceProvider ServiceProvider { get; }
    public AppPaths Paths { get; }
    public LibraryDatabase Db { get; }
    public LibraryManager Library { get; }
    public LanguagePluginRegistry Languages { get; }
    public ChapterEnrichmentCache EnrichmentCache { get; }
    public VocabularyTracker VocabTracker { get; }
    public BookSchemeHandler BookScheme { get; }
    public DictionaryDatabase DictDb { get; }
    public DictionaryLookupEngine LookupEngine { get; }
    public PythonStudyPackBridge StudyPackBridge { get; }
    public FlashcardMiningService Flashcards { get; }
    public ReaderContextService ReaderContext { get; }
    public SnippetEnricher SnippetEnricher { get; }
    public VocabularyBudgetService VocabBudget { get; }
    public ConfigurationManager Config { get; }
    public FeaturePluginHost PluginHost { get; }
    public IReadOnlyList<IPluginModule> Modules { get; }

    private AppServices(
        IServiceProvider serviceProvider,
        AppPaths paths,
        LibraryDatabase db,
        LibraryManager library,
        LanguagePluginRegistry languages,
        ChapterEnrichmentCache enrichmentCache,
        VocabularyTracker vocabTracker,
        BookSchemeHandler bookScheme,
        DictionaryDatabase dictDb,
        DictionaryLookupEngine lookupEngine,
        PythonStudyPackBridge studyPackBridge,
        FlashcardMiningService flashcards,
        ReaderContextService readerContext,
        SnippetEnricher snippetEnricher,
        VocabularyBudgetService vocabBudget,
        ConfigurationManager config,
        FeaturePluginHost pluginHost,
        IReadOnlyList<IPluginModule> modules)
    {
        ServiceProvider = serviceProvider;
        Paths = paths;
        Db = db;
        Library = library;
        Languages = languages;
        EnrichmentCache = enrichmentCache;
        VocabTracker = vocabTracker;
        BookScheme = bookScheme;
        DictDb = dictDb;
        LookupEngine = lookupEngine;
        StudyPackBridge = studyPackBridge;
        Flashcards = flashcards;
        ReaderContext = readerContext;
        SnippetEnricher = snippetEnricher;
        VocabBudget = vocabBudget;
        Config = config;
        PluginHost = pluginHost;
        Modules = modules;
    }

    public static AppServices Create(AppPaths paths, IEnumerable<IPluginModule> modules)
    {
        var moduleList = modules.ToList();
        var services = new ServiceCollection();

        // 1. Core infrastructure
        services.AddSingleton(paths);
        var db = new LibraryDatabase(paths.DbPath, paths.CoversDir);
        db.InitializeSchema();
        services.AddSingleton(db);
        services.AddSingleton<IDbConnectionFactory>(db);

        services.AddSingleton(sp => new LibraryManager(sp.GetRequiredService<LibraryDatabase>(), paths.ExtractorOutputDir));
        services.AddSingleton<IBookCatalog>(sp => sp.GetRequiredService<LibraryManager>());

        // 2. Language Registry
        var languages = new LanguagePluginRegistry();
        foreach (var module in moduleList)
        {
            module.RegisterLanguages(languages);
        }
        languages.Register(new WhitespaceLanguagePlugin(), isFallback: true);
        services.AddSingleton(languages);
        services.AddSingleton<ILanguagePluginResolver>(languages);

        // 3. Caches & Dictionaries
        services.AddSingleton(new ChapterEnrichmentCache(maxEntries: 30));
        services.AddSingleton<IChapterTokenMapProvider>(sp => sp.GetRequiredService<ChapterEnrichmentCache>());

        services.AddSingleton(sp => new VocabularyTracker(sp.GetRequiredService<LibraryDatabase>()));
        services.AddSingleton<IVocabularyService>(sp => sp.GetRequiredService<VocabularyTracker>());

        services.AddSingleton(sp => new BookSchemeHandler(
            sp.GetRequiredService<LibraryManager>(),
            sp.GetRequiredService<LanguagePluginRegistry>(),
            sp.GetRequiredService<ChapterEnrichmentCache>(),
            getWordStatus: (lemma, reading) => sp.GetRequiredService<VocabularyTracker>().GetStatus("ja", lemma, reading)));

        var dictDb = new DictionaryDatabase(paths.DictDbPath);
        services.AddSingleton(dictDb);
        services.AddSingleton(sp => new DictionaryLookupEngine(
            sp.GetRequiredService<DictionaryDatabase>(),
            sp.GetRequiredService<ChapterEnrichmentCache>(),
            sp.GetRequiredService<VocabularyTracker>(),
            sp.GetRequiredService<LanguagePluginRegistry>().Resolve(LanguageCodes.Japanese).GetDeinflector()));

        // 4. Study Pack & Flashcards
        services.AddSingleton(new PythonStudyPackBridge(paths.ExtractorOutputDir, paths.ExtractorDir));
        services.AddSingleton<IStudyPackCatalog>(sp => sp.GetRequiredService<PythonStudyPackBridge>());
        services.AddSingleton(new FlashcardMiningService(paths.ExtractorOutputDir));

        // 5. Enrichment & Slicing
        services.AddSingleton(sp => new ReaderContextService(
            sp.GetRequiredService<LibraryManager>(),
            sp.GetRequiredService<ChapterEnrichmentCache>(),
            sp.GetRequiredService<LibraryDatabase>(),
            sp.GetRequiredService<BookSchemeHandler>()));
        services.AddSingleton<IReaderContextService>(sp => sp.GetRequiredService<ReaderContextService>());

        services.AddSingleton(sp => new SnippetEnricher(
            sp.GetRequiredService<LanguagePluginRegistry>(),
            sp.GetRequiredService<ChapterEnrichmentCache>(),
            getWordStatus: (lemma, reading) => sp.GetRequiredService<VocabularyTracker>().GetStatus("ja", lemma, reading)));
        services.AddSingleton<ISnippetEnricher>(sp => sp.GetRequiredService<SnippetEnricher>());

        services.AddSingleton(sp => new VocabularyBudgetService(
            sp.GetRequiredService<LibraryDatabase>(),
            sp.GetRequiredService<PythonStudyPackBridge>()));
        services.AddSingleton<IVocabularyBudgetService>(sp => sp.GetRequiredService<VocabularyBudgetService>());

        // 6. Global Configuration
        services.AddSingleton(sp => new ConfigurationManager(
            sp.GetRequiredService<LibraryDatabase>(),
            paths.ExtractorBooksDir));
        services.AddSingleton<IConfigurationManager>(sp => sp.GetRequiredService<ConfigurationManager>());

        // 7. Plugin Host
        services.AddSingleton(sp => new FeaturePluginHost(
            sp.GetRequiredService<IReaderContextService>(),
            sp.GetRequiredService<IVocabularyBudgetService>(),
            sp.GetRequiredService<ISnippetEnricher>()));

        // 8. Allow modules to configure their own container services
        foreach (var module in moduleList)
        {
            module.ConfigureServices(services);
        }

        var provider = services.BuildServiceProvider();

        // 9. Instantiate & Register feature plugins
        var pluginHost = provider.GetRequiredService<FeaturePluginHost>();
        var readerContext = provider.GetRequiredService<ReaderContextService>();
        var vocabBudget = provider.GetRequiredService<VocabularyBudgetService>();
        var snippetEnricher = provider.GetRequiredService<SnippetEnricher>();
        var config = provider.GetRequiredService<ConfigurationManager>();

        var pluginServices = new PluginServices(
            readerContext,
            vocabBudget,
            snippetEnricher,
            languages,
            config,
            provider);

        foreach (var module in moduleList)
        {
            var plugins = module.CreateFeaturePlugins(provider);
            if (plugins.Count == 0)
            {
                plugins = module.CreateFeaturePlugins(pluginServices);
            }

            foreach (var plugin in plugins)
            {
                pluginHost.RegisterPluginAsync(plugin).GetAwaiter().GetResult();
            }
        }

        return new AppServices(
            serviceProvider: provider,
            paths: paths,
            db: db,
            library: provider.GetRequiredService<LibraryManager>(),
            languages: languages,
            enrichmentCache: provider.GetRequiredService<ChapterEnrichmentCache>(),
            vocabTracker: provider.GetRequiredService<VocabularyTracker>(),
            bookScheme: provider.GetRequiredService<BookSchemeHandler>(),
            dictDb: dictDb,
            lookupEngine: provider.GetRequiredService<DictionaryLookupEngine>(),
            studyPackBridge: provider.GetRequiredService<PythonStudyPackBridge>(),
            flashcards: provider.GetRequiredService<FlashcardMiningService>(),
            readerContext: readerContext,
            snippetEnricher: snippetEnricher,
            vocabBudget: vocabBudget,
            config: config,
            pluginHost: pluginHost,
            modules: moduleList);
    }

    public IReadOnlyList<Rpc.IRpcModule> CreateRpcModules(
        Action<string>? sendMessageToUi = null,
        Action<bool, string>? onSelfTestCompleted = null)
    {
        var events = new Rpc.DelegateRpcEventSink(sendMessageToUi);
        return
        [
            new Rpc.Modules.BootstrapRpcModule(Library, Config, Paths.ExtractorBooksDir, DictDb),
            new Rpc.Modules.LibraryRpcModule(Library, events),
            new Rpc.Modules.JitenRpcModule(Library),
            new Rpc.Modules.ReaderRpcModule(Library, BookScheme, LookupEngine, VocabTracker, Db, events),
            new Rpc.Modules.DictionaryRpcModule(DictDb, events: events),
            new Rpc.Modules.StudyPackRpcModule(Library, StudyPackBridge, Flashcards, events, Config),
            new Rpc.Modules.ConfigRpcModule(Config, events),
            new Rpc.Modules.ExtensionsRpcModule(PluginHost, onSelfTestCompleted)
        ];
    }

    public void Dispose()
    {
        PluginHost.Dispose();

        foreach (var module in Modules)
        {
            module.Dispose();
        }

        if (ServiceProvider is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }

        DictDb.Dispose();
        Languages.Dispose();
        Db.Dispose();
    }
}
