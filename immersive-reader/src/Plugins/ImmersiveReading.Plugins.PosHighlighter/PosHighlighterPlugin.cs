using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosHighlighterPlugin : IImmersivePlugin, IDisposable
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

        // Reader menu (spec 10): catalog + current selection, live CSS, and selection updates.
        context.RegisterRpcAction("getCategories", (payload, token) =>
            Task.FromResult<object?>(BuildCategoriesState()));

        context.RegisterRpcAction("getCss", (payload, token) =>
            Task.FromResult<object?>(new { css = CurrentCss() }));

        context.RegisterRpcAction("setCategories", async (payload, token) =>
        {
            if (payload.ValueKind == JsonValueKind.Object)
            {
                if (payload.TryGetProperty("enabledCategories", out var catsProp) && catsProp.ValueKind == JsonValueKind.Array)
                {
                    var ids = catsProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString());
                    _settings.EnabledCategories = PosCategoryCatalog.Sanitize(ids);
                }

                if (payload.TryGetProperty("enabled", out var enProp) &&
                    enProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    _settings.Enabled = enProp.GetBoolean();
                }
            }

            if (_stage != null)
            {
                _stage.Settings = _settings;
            }

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(_settings));
            await context.Config.UpdateAsync(doc.RootElement, token);

            return new
            {
                success = true,
                enabled = _settings.Enabled,
                enabledCategories = _settings.EnabledCategories ?? PosCategoryCatalog.All.Select(c => c.Id).ToList(),
                css = CurrentCss()
            };
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

    /// <summary>CSS for the open chapter; empty when highlighting is switched off.</summary>
    private string CurrentCss() => _settings.Enabled ? PosDomEnrichmentStage.GenerateCss(_settings) : "";

    private object BuildCategoriesState() => new
    {
        enabled = _settings.Enabled,
        highlightStyle = _settings.HighlightStyle,
        categories = PosCategoryCatalog.All.Select(c => new
        {
            id = c.Id,
            label = c.Label,
            jpLabel = c.JpLabel,
            example = c.Example,
            color = c.ColorOf(_settings),
            enabled = _settings.IsCategoryEnabled(c.Id)
        }).ToList()
    };

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        _configSub?.Dispose();
        _configSub = null;
        _stage = null;
        _cache?.Dispose();
        _cache = null;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _configSub?.Dispose();
        _configSub = null;
        _stage = null;
        _cache?.Dispose();
        _cache = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
