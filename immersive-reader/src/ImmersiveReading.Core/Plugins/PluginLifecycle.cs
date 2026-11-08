namespace ImmersiveReading.Core.Plugins;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginReloadBehavior
{
    /// <summary>
    /// Can be enabled/disabled at runtime without restarting the application.
    /// UI bundles, RPC handlers, and event subscriptions are dynamically mounted/unmounted.
    /// </summary>
    Hot,

    /// <summary>
    /// Toggling state takes effect on next application startup (e.g., morphological tokenizers).
    /// </summary>
    RestartRequired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginRuntimeState
{
    Disabled,
    Enabled,
    PendingEnable,
    PendingDisable,
    Faulted
}

public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Author { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PluginReloadBehavior ReloadBehavior { get; init; } = PluginReloadBehavior.Hot;
    public string? UiEntry { get; init; }
    public string? StyleEntry { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}
