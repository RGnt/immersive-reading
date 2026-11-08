using System;
using System.Collections.Generic;
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
