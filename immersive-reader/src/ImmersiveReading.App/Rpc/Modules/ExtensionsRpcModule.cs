using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ImmersiveReading.App.Plugins;
using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class ExtensionsRpcModule(
    PluginHostManager? pluginHostManager = null,
    FeaturePluginHost? featurePluginHost = null,
    Action<bool, string>? onSelfTestCompleted = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("extensions.list", (req, _) => Task.FromResult<object?>(ListExtensions()));
            // Plugin UI code/styles are fetched over RPC (not ir-app://): the SPA runs from file://, where the
            // WebView blocks custom-scheme fetch/import. See specs/11-plugin-ui-loading-spec.md.
            yield return ("extensions.getAsset", (req, _) =>
            {
                if (pluginHostManager is null)
                {
                    throw new InvalidOperationException("Plugin host is not available.");
                }

                return Task.FromResult<object?>(
                    ReadPluginAsset(pluginHostManager, req.RequiredString("pluginId"), req.RequiredString("path")));
            });
            yield return ("extensions.setEnabled", async (req, ct) =>
            {
                var pluginId = req.RequiredString("pluginId");
                var enabled = req.OptionalBool("enabled") ?? true;
                if (pluginHostManager is not null)
                {
                    var result = await pluginHostManager.SetPluginEnabledAsync(pluginId, enabled, ct);
                    return new { ok = true, state = result.State.ToString(), requiresRestart = result.RequiresRestart };
                }
                return new { ok = false, state = "Disabled", requiresRestart = false };
            });
            yield return ("extensions.getConfig", (req, _) =>
            {
                var pluginId = req.RequiredString("pluginId");
                if (pluginHostManager is not null)
                {
                    var config = pluginHostManager.GetPluginConfig(pluginId).GetRawJson();
                    return Task.FromResult<object?>(new { pluginId, config });
                }
                return Task.FromResult<object?>(new { pluginId, config = (object?)null });
            });
            yield return ("extensions.updateConfig", async (req, ct) =>
            {
                var pluginId = req.RequiredString("pluginId");
                if (req.Params.TryGetProperty("config", out var config) && pluginHostManager is not null)
                {
                    await pluginHostManager.GetPluginConfig(pluginId).UpdateAsync(config, ct);
                    return new { ok = true };
                }
                return new { ok = false };
            });
            yield return ("extensions.resetConfig", async (req, ct) =>
            {
                var pluginId = req.RequiredString("pluginId");
                if (pluginHostManager is not null)
                {
                    await pluginHostManager.GetPluginConfig(pluginId).ResetToDefaultsAsync(ct);
                    return new { ok = true };
                }
                return new { ok = false };
            });
            yield return ("ui.reportSelfTestResult", (req, _) => Task.FromResult<object?>(ReportSelfTestResult(req)));
        }
    }

    private static readonly HashSet<string> AllowedAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".css", ".json"
    };

    /// <summary>Largest plugin asset the UI loader may read (NFR-2 in spec 11).</summary>
    public const int MaxAssetBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Reads a text asset (UI script, stylesheet, json) belonging to one plugin. Confined to that plugin's
    /// own assets: no parent segments, no rooted paths, allow-listed extensions, size capped.
    /// </summary>
    public static object ReadPluginAsset(PluginHostManager host, string pluginId, string path)
    {
        string normalized = path.Replace('\\', '/').Trim();
        if (normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Split('/').Any(seg => seg == ".." || seg == "."))
        {
            throw new ArgumentException($"Invalid plugin asset path '{path}'.");
        }

        string ext = Path.GetExtension(normalized);
        if (!AllowedAssetExtensions.Contains(ext))
        {
            throw new ArgumentException($"Plugin asset type '{ext}' is not allowed (allowed: .js .mjs .css .json).");
        }

        using var stream = host.OpenAssetStream(pluginId, normalized)
            ?? throw new KeyNotFoundException($"Asset '{normalized}' not found in plugin '{pluginId}'.");

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxAssetBytes)
            {
                throw new InvalidOperationException($"Asset '{normalized}' exceeds the {MaxAssetBytes / (1024 * 1024)} MB limit.");
            }
        }

        string contentType = ext.ToLowerInvariant() switch
        {
            ".css" => "text/css",
            ".json" => "application/json",
            _ => "text/javascript"
        };
        return new { path = normalized, contentType, text = System.Text.Encoding.UTF8.GetString(buffer.ToArray()) };
    }

        private object? ListExtensions()
    {
        var plugins = pluginHostManager is not null
            ? pluginHostManager.GetPluginDescriptors()
            : (featurePluginHost?.Plugins.Select(pl => (object)new { pl.PluginId, pl.DisplayName, pl.Version }).ToArray() ?? []);

        var uiPanels = featurePluginHost?.UiPanels ?? (IReadOnlyList<ReaderUiPanelDescriptor>)[];

        return new
        {
            plugins,
            uiPanels
        };
    }

    private object? ReportSelfTestResult(RpcRequest req)
    {
        bool passed = req.OptionalBool("passed") ?? false;
        string summary = req.OptionalString("summary") ?? "";
        onSelfTestCompleted?.Invoke(passed, summary);
        return new { ok = true };
    }
}
