using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosHighlighterPlugin : IImmersivePlugin
{
    public const string PluginIdValue = "posHighlighter";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdValue,
        Name = "Part-of-Speech Highlighter",
        Version = "1.0.0",
        Author = "RGnt",
        Description = "Visual grammar colorizer highlighting i/na-adjectives, u/ru/irregular verbs, suffixes, and particles with customizable color palettes.",
        ReloadBehavior = PluginReloadBehavior.Hot,
        UiEntry = "ui.js"
    };

    private PosHighlighterSettings _settings = new();
    private PosDomEnrichmentStage? _stage;
    private ChapterPosCache? _cache;
    private IDisposable? _configSub;

    public PosHighlighterSettings CurrentSettings => _settings;
    public PosDomEnrichmentStage? ActiveStage => _stage;
    public ChapterPosCache? ActiveCache => _cache;

    public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        // 1. Load initial settings
        _settings = context.Config.Get<PosHighlighterSettings>();

        // 2. Instantiate and register DOM enrichment stage with persistent cache
        _cache = new ChapterPosCache(context.Database?.DatabaseFilePath, maxCacheSizeMb: _settings.MaxCacheSizeMb);
        _stage = new PosDomEnrichmentStage(_settings, cache: _cache);
        context.RegisterDomEnrichmentStage(_stage);

        // 3. Register RPC actions
        context.RegisterRpcAction("getSettings", (payload, token) =>
        {
            return Task.FromResult<object?>(_settings);
        });

        context.RegisterRpcAction("clearCache", (payload, token) =>
        {
            _cache?.Clear();
            return Task.FromResult<object?>(new { success = true });
        });

        context.RegisterRpcAction("getCacheStats", (payload, token) =>
        {
            var (count, bytes) = _cache?.GetStats() ?? (0, 0L);
            return Task.FromResult<object?>(new
            {
                count,
                bytes,
                maxMb = _settings.MaxCacheSizeMb
            });
        });

        context.RegisterRpcAction("toggle", async (payload, token) =>
        {
            bool nextEnabled = !_settings.Enabled;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("enabled", out var enProp))
            {
                nextEnabled = enProp.GetBoolean();
            }

            _settings.Enabled = nextEnabled;
            if (_stage != null)
            {
                _stage.Settings = _settings;
            }

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(_settings));
            await context.Config.UpdateAsync(doc.RootElement, token);

            return new { enabled = nextEnabled, success = true };
        });

        context.RegisterRpcAction("updateSettings", async (payload, token) =>
        {
            var updated = PosHighlighterSettings.FromJson(payload);
            _settings = updated;
            if (_stage != null)
            {
                _stage.Settings = _settings;
            }

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(_settings));
            await context.Config.UpdateAsync(doc.RootElement, token);

            return new { success = true, settings = _settings };
        });

        // 4. Subscribe to runtime configuration updates from Settings UI
        _configSub = context.Config.OnConfigChanged<PosHighlighterSettings>(updated =>
        {
            _settings = updated;
            if (_stage != null)
            {
                _stage.Settings = _settings;
            }
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        _configSub?.Dispose();
        _configSub = null;
        _stage = null;
        _cache?.Dispose();
        _cache = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _configSub?.Dispose();
        _configSub = null;
        _stage = null;
        _cache?.Dispose();
        _cache = null;
        return ValueTask.CompletedTask;
    }
}
