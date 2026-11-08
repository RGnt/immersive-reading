namespace ImmersiveReading.Core.Plugins;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IImmersivePlugin : IAsyncDisposable
{
    PluginManifest Manifest { get; }

    /// <summary>
    /// Invoked when the plugin is enabled (at startup or hot-enabled from Settings).
    /// </summary>
    ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default);

    /// <summary>
    /// Invoked when the plugin is disabled at runtime or during application shutdown.
    /// All IDisposable registrations made on IPluginHostContext are automatically cleaned up by the host.
    /// </summary>
    ValueTask OnDisableAsync(CancellationToken ct = default);
}
