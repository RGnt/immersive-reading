using System.Text.Json;
using ImmersiveReading.App.Composition;
using ImmersiveReading.App.Plugins;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc;

public sealed class LibraryRpcDispatcher : IDisposable
{
    private readonly Dictionary<string, RpcHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly FeaturePluginHost? _featurePluginHost;
    private readonly PluginHostManager? _pluginHostManager;
    private readonly List<IDisposable> _disposables = [];

    public LibraryRpcDispatcher(
        IEnumerable<IRpcModule> modules,
        FeaturePluginHost? featurePluginHost = null,
        PluginHostManager? pluginHostManager = null)
    {
        _featurePluginHost = featurePluginHost;
        _pluginHostManager = pluginHostManager;
        foreach (var module in modules)
        {
            if (module is IDisposable d)
            {
                _disposables.Add(d);
            }
            foreach (var (method, handler) in module.Handlers)
            {
                if (_handlers.ContainsKey(method))
                {
                    throw new InvalidOperationException($"RPC method '{method}' is already registered.");
                }
                _handlers[method] = handler;
            }
        }
    }

    public LibraryRpcDispatcher(
        AppServices services,
        Action<string>? sendMessageToUi = null,
        Action<bool, string>? onSelfTestCompleted = null,
        Func<string?, string?>? folderPicker = null)
        : this(services.CreateRpcModules(sendMessageToUi, onSelfTestCompleted, folderPicker), services.PluginHost, services.PluginHostManager)
    {
    }

    public LibraryRpcDispatcher(
        LibraryManager library,
        LibraryDatabase? db = null,
        BookSchemeHandler? bookSchemeHandler = null,
        string? workspaceBooksDir = null,
        Action<string>? sendMessageToUi = null,
        DictionaryLookupEngine? lookupEngine = null,
        VocabularyTracker? vocabTracker = null,
        DictionaryDatabase? dictDb = null,
        PythonStudyPackBridge? studyPackBridge = null,
        FlashcardMiningService? flashcardService = null,
        FeaturePluginHost? featurePluginHost = null,
        Action<bool, string>? onSelfTestCompleted = null,
        IConfigurationManager? configManager = null)
        : this(
            BuildLegacyModules(
                library,
                db,
                bookSchemeHandler,
                workspaceBooksDir,
                sendMessageToUi,
                lookupEngine,
                vocabTracker,
                dictDb,
                studyPackBridge,
                flashcardService,
                featurePluginHost,
                onSelfTestCompleted,
                configManager),
            featurePluginHost)
    {
    }

    private static IEnumerable<IRpcModule> BuildLegacyModules(
        LibraryManager library,
        LibraryDatabase? db,
        BookSchemeHandler? bookSchemeHandler,
        string? workspaceBooksDir,
        Action<string>? sendMessageToUi,
        DictionaryLookupEngine? lookupEngine,
        VocabularyTracker? vocabTracker,
        DictionaryDatabase? dictDb,
        PythonStudyPackBridge? studyPackBridge,
        FlashcardMiningService? flashcardService,
        FeaturePluginHost? featurePluginHost,
        Action<bool, string>? onSelfTestCompleted,
        IConfigurationManager? configManager)
    {
        var events = new DelegateRpcEventSink(sendMessageToUi);
        yield return new Modules.BootstrapRpcModule(library, configManager, workspaceBooksDir, dictDb);
        yield return new Modules.LibraryRpcModule(library, events);
        yield return new Modules.JitenRpcModule(library);
        yield return new Modules.ReaderRpcModule(library, bookSchemeHandler, lookupEngine, vocabTracker, db, events, dictDb: dictDb);
        yield return new Modules.DictionaryRpcModule(dictDb);
        yield return new Modules.StudyPackRpcModule(library, studyPackBridge, flashcardService, events, configManager);
        yield return new Modules.ConfigRpcModule(configManager, events);
        yield return new Modules.ExtensionsRpcModule(pluginHostManager: null, featurePluginHost: featurePluginHost, onSelfTestCompleted: onSelfTestCompleted);
    }

    public async Task<string> DispatchJsonAsync(string rawRequestJson, CancellationToken ct = default)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(rawRequestJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idProp))
            {
                id = idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : idProp.GetRawText();
            }

            string method = root.TryGetProperty("method", out var mProp) ? (mProp.GetString() ?? "") : "";
            JsonElement @params = root.TryGetProperty("params", out var pProp) ? pProp : default;

            object? result = await HandleMethodAsync(method, @params, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { id, result, error = (object?)null }, RpcJson.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                result = (object?)null,
                error = new { message = ex.Message, type = ex.GetType().Name }
            }, RpcJson.Options);
        }
    }

    public async Task<object?> HandleMethodAsync(string method, JsonElement p, CancellationToken ct = default)
    {
        if (_handlers.TryGetValue(method, out var handler))
        {
            return await handler(new RpcRequest(method, p), ct).ConfigureAwait(false);
        }

        if (_pluginHostManager is not null && _pluginHostManager.TryGetRpcHandler(method, out var unifiedHandler) && unifiedHandler is not null)
        {
            return await unifiedHandler(p, ct).ConfigureAwait(false);
        }

        if (_featurePluginHost is not null && _featurePluginHost.TryGetRpcHandler(method, out var extHandler) && extHandler is not null)
        {
            return await extHandler(p, ct).ConfigureAwait(false);
        }

        throw new NotSupportedException($"Unknown RPC method: '{method}'.");
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
        _disposables.Clear();
    }
}
