using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class ExtensionsRpcModule(
    FeaturePluginHost? featurePluginHost = null,
    Action<bool, string>? onSelfTestCompleted = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("extensions.list", (req, _) => Task.FromResult<object?>(ListExtensions()));
            yield return ("ui.reportSelfTestResult", (req, _) => Task.FromResult<object?>(ReportSelfTestResult(req)));
        }
    }

    private object? ListExtensions()
    {
        if (featurePluginHost is null)
        {
            return new { plugins = Array.Empty<object>(), uiPanels = Array.Empty<object>() };
        }
        return new
        {
            plugins = featurePluginHost.Plugins.Select(pl => new { pl.PluginId, pl.DisplayName, pl.Version }).ToArray(),
            uiPanels = featurePluginHost.UiPanels
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

