namespace ImmersiveReading.App.Plugins;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

public sealed class PluginHostManager : IAsyncDisposable
{
    private readonly string _dataRootDir;
    private readonly IBookCatalog _bookCatalog;
    private readonly IReaderContextService _readerContext;
    private readonly IVocabularyBudgetService _vocabBudget;
    private readonly ISnippetEnricher _snippetEnricher;
    private readonly LanguagePluginRegistry _languageRegistry;
    private readonly IRpcEventSink _rpcEvents;
    private readonly PluginEventBusHub _eventBusHub;

    private readonly Dictionary<string, PluginEntry> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginRpcHandler> _rpcHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDomEnrichmentStage> _domStages = [];
    private readonly string _enabledStateFilePath;

    public PluginHostManager(
        string dataRootDir,
        IBookCatalog bookCatalog,
        IReaderContextService readerContext,
        IVocabularyBudgetService vocabBudget,
        ISnippetEnricher snippetEnricher,
        LanguagePluginRegistry languageRegistry,
        IRpcEventSink rpcEvents)
    {
        _dataRootDir = dataRootDir;
        _bookCatalog = bookCatalog;
        _readerContext = readerContext;
        _vocabBudget = vocabBudget;
        _snippetEnricher = snippetEnricher;
        _languageRegistry = languageRegistry;
        _rpcEvents = rpcEvents;
        _eventBusHub = new PluginEventBusHub(rpcEvents);
        _enabledStateFilePath = Path.Combine(_dataRootDir, "config", "plugins-state.json");
    }

    public sealed class PluginEntry
    {
        public required IImmersivePlugin Instance { get; init; }
        public required PluginConfigContext Config { get; init; }
        public required PluginDatabaseContext Database { get; init; }
        public required string PluginDataDir { get; init; }
        public string? ExternalPluginFolder { get; init; }
        public PluginRuntimeState State { get; set; }
        public ScopedPluginHostContext? ActiveContext { get; set; }
        public string? LastError { get; set; }
    }

    public async ValueTask InitializePluginsAsync(
        IEnumerable<IImmersivePlugin> discoveredPlugins,
        CancellationToken ct = default)
    {
        var savedStates = LoadPersistedEnabledMap();
        var configDir = Path.Combine(_dataRootDir, "config", "plugins");

        foreach (var plugin in discoveredPlugins)
        {
            var id = plugin.Manifest.Id;
            var pluginDataDir = Path.Combine(_dataRootDir, "plugins", id);

            var configCtx = new PluginConfigContext(
                id,
                configDir,
                relPath => OpenPluginAssetStream(plugin, null, relPath),
                (pId, updatedJson) => _rpcEvents.Emit("extensions.configChanged", new { pluginId = pId, config = updatedJson }));

            var dbCtx = new PluginDatabaseContext(pluginDataDir);
            var shouldEnable = !savedStates.TryGetValue(id, out var enabled) || enabled;

            var entry = new PluginEntry
            {
                Instance = plugin,
                Config = configCtx,
                Database = dbCtx,
                PluginDataDir = pluginDataDir,
                State = PluginRuntimeState.Disabled
            };

            _plugins[id] = entry;

            if (shouldEnable)
            {
                await EnablePluginInternalAsync(entry, ct);
            }
        }
    }

    public async ValueTask<(PluginRuntimeState State, bool RequiresRestart)> SetPluginEnabledAsync(
        string pluginId,
        bool enabled,
        CancellationToken ct = default)
    {
        if (!_plugins.TryGetValue(pluginId, out var entry))
            throw new KeyNotFoundException($"Plugin '{pluginId}' not found.");

        var persistedMap = LoadPersistedEnabledMap();
        persistedMap[pluginId] = enabled;
        SavePersistedEnabledMap(persistedMap);

        var isHot = entry.Instance.Manifest.ReloadBehavior == PluginReloadBehavior.Hot;

        if (!isHot)
        {
            entry.State = enabled switch
            {
                true when entry.State == PluginRuntimeState.Disabled => PluginRuntimeState.PendingEnable,
                true when entry.State == PluginRuntimeState.PendingDisable => PluginRuntimeState.Enabled,
                false when entry.State == PluginRuntimeState.Enabled => PluginRuntimeState.PendingDisable,
                false when entry.State == PluginRuntimeState.PendingEnable => PluginRuntimeState.Disabled,
                _ => entry.State
            };

            var requiresRestart = entry.State is PluginRuntimeState.PendingEnable or PluginRuntimeState.PendingDisable;
            EmitStateChanged(entry, requiresRestart);
            return (entry.State, requiresRestart);
        }

        if (enabled && entry.State != PluginRuntimeState.Enabled)
            await EnablePluginInternalAsync(entry, ct);
        else if (!enabled && entry.State == PluginRuntimeState.Enabled)
            await DisablePluginInternalAsync(entry, ct);

        EmitStateChanged(entry, requiresRestart: false);
        return (entry.State, false);
    }

    private async ValueTask EnablePluginInternalAsync(PluginEntry entry, CancellationToken ct)
    {
        var scopedCtx = new ScopedPluginHostContext(this, entry);
        try
        {
            await entry.Instance.OnEnableAsync(scopedCtx, ct);
            entry.ActiveContext = scopedCtx;
            entry.State = PluginRuntimeState.Enabled;
            entry.LastError = null;
        }
        catch (Exception ex)
        {
            scopedCtx.DisposeAllRegistrations();
            entry.State = PluginRuntimeState.Faulted;
            entry.LastError = ex.Message;
        }
    }

    private async ValueTask DisablePluginInternalAsync(PluginEntry entry, CancellationToken ct)
    {
        try
        {
            await entry.Instance.OnDisableAsync(ct);
        }
        finally
        {
            entry.ActiveContext?.DisposeAllRegistrations();
            entry.ActiveContext = null;
            entry.State = PluginRuntimeState.Disabled;
        }
    }

    public bool TryGetRpcHandler(string fullMethodName, out PluginRpcHandler handler)
        => _rpcHandlers.TryGetValue(fullMethodName, out handler!);

    public Stream? OpenAssetStream(string pluginId, string relativePath)
    {
        if (!_plugins.TryGetValue(pluginId, out var entry))
            return null;

        return OpenPluginAssetStream(entry.Instance, entry.ExternalPluginFolder, relativePath);
    }

    private static Stream? OpenPluginAssetStream(IImmersivePlugin plugin, string? externalDir, string relativePath)
    {
        var sanitized = relativePath.TrimStart('/', '\\').Replace('\\', '/');

        if (!string.IsNullOrEmpty(externalDir))
        {
            var diskPath = Path.Combine(externalDir, sanitized);
            if (File.Exists(diskPath))
                return File.OpenRead(diskPath);
        }

        var asm = plugin.GetType().Assembly;
        var resourcePathSuffix = "." + sanitized.Replace('/', '.');
        var matchingRes = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourcePathSuffix, StringComparison.OrdinalIgnoreCase));

        return matchingRes is not null ? asm.GetManifestResourceStream(matchingRes) : null;
    }

    public IReadOnlyList<object> GetPluginDescriptors()
    {
        return _plugins.Values.Select(entry => new
        {
            manifest = entry.Instance.Manifest,
            state = entry.State.ToString(),
            requiresRestart = entry.State is PluginRuntimeState.PendingEnable or PluginRuntimeState.PendingDisable,
            lastError = entry.LastError,
            uiBundleUrl = entry.Instance.Manifest.UiEntry is { } ui
                ? $"ir-app://plugins/{entry.Instance.Manifest.Id}/{ui}"
                : null,
            styleBundleUrl = entry.Instance.Manifest.StyleEntry is { } css
                ? $"ir-app://plugins/{entry.Instance.Manifest.Id}/{css}"
                : null,
            config = entry.Config.GetRawJson(),
            schema = entry.Config.GetSchemaJson()
        }).ToList();
    }

    public PluginConfigContext GetPluginConfig(string pluginId)
        => _plugins.TryGetValue(pluginId, out var e)
            ? e.Config
            : throw new KeyNotFoundException($"Plugin '{pluginId}' not found.");

    private void EmitStateChanged(PluginEntry entry, bool requiresRestart)
    {
        _rpcEvents.Emit("extensions.stateChanged", new
        {
            pluginId = entry.Instance.Manifest.Id,
            state = entry.State.ToString(),
            requiresRestart,
            uiBundleUrl = entry.Instance.Manifest.UiEntry is { } ui
                ? $"ir-app://plugins/{entry.Instance.Manifest.Id}/{ui}"
                : null
        });
    }

    private Dictionary<string, bool> LoadPersistedEnabledMap()
    {
        if (!File.Exists(_enabledStateFilePath))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = File.ReadAllText(_enabledStateFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(json)
                   ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SavePersistedEnabledMap(Dictionary<string, bool> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_enabledStateFilePath)!);
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_enabledStateFilePath, json);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _plugins.Values)
        {
            if (entry.State == PluginRuntimeState.Enabled)
            {
                await DisablePluginInternalAsync(entry, CancellationToken.None);
            }
            await entry.Instance.DisposeAsync();
        }
        _plugins.Clear();
        _rpcHandlers.Clear();
        _domStages.Clear();
    }

    public sealed class ScopedPluginHostContext : IPluginHostContext
    {
        private readonly PluginHostManager _host;
        private readonly PluginEntry _entry;
        private readonly List<IDisposable> _disposables = [];

        public ScopedPluginHostContext(PluginHostManager host, PluginEntry entry)
        {
            _host = host;
            _entry = entry;
            EventBus = _host._eventBusHub.CreateScopedBus(Manifest.Id);
        }

        public PluginManifest Manifest => _entry.Instance.Manifest;
        public string PluginDataDirectory => _entry.PluginDataDir;
        public IPluginDatabaseContext Database => _entry.Database;
        public IPluginConfigContext Config => _entry.Config;
        public IPluginEventBus EventBus { get; }

        public IBookCatalog BookCatalog => _host._bookCatalog;
        public IReaderContextService ReaderContext => _host._readerContext;
        public IVocabularyBudgetService VocabularyBudget => _host._vocabBudget;
        public ISnippetEnricher SnippetEnricher => _host._snippetEnricher;

        public IDisposable RegisterRpcAction(string actionName, PluginRpcHandler handler)
        {
            var fullKey = $"ext.{Manifest.Id}.{actionName}";
            _host._rpcHandlers[fullKey] = handler;
            return TrackDisposable(() => _host._rpcHandlers.TryRemove(fullKey, out _));
        }

        public IDisposable RegisterLanguageAnalyzer(ILanguagePlugin languagePlugin, bool isFallback = false)
        {
            _host._languageRegistry.Register(languagePlugin, isFallback);
            return TrackDisposable(() => _host._languageRegistry.Unregister(languagePlugin.LanguageCode));
        }

        public IDisposable RegisterDomEnrichmentStage(IDomEnrichmentStage stage)
        {
            lock (_host._domStages)
            {
                _host._domStages.Add(stage);
                _host._domStages.Sort((a, b) => a.Order.CompareTo(b.Order));
            }
            return TrackDisposable(() =>
            {
                lock (_host._domStages) { _host._domStages.Remove(stage); }
            });
        }

        public Stream? OpenAssetStream(string relativeAssetPath)
            => _host.OpenAssetStream(Manifest.Id, relativeAssetPath);

        private IDisposable TrackDisposable(Action onDispose)
        {
            var disp = new CallbackDisposable(onDispose);
            _disposables.Add(disp);
            return disp;
        }

        public void DisposeAllRegistrations()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
                _disposables[i].Dispose();
            _disposables.Clear();
            (EventBus as IDisposable)?.Dispose();
        }
    }
}

public sealed class CallbackDisposable(Action onDispose) : IDisposable
{
    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            onDispose();
    }
}
