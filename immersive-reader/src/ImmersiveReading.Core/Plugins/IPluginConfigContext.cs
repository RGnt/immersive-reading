namespace ImmersiveReading.Core.Plugins;

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public interface IPluginConfigContext
{
    T Get<T>() where T : class, new();
    JsonElement GetRawJson();
    JsonElement? GetSchemaJson();
    ValueTask UpdateAsync(JsonElement partialOrFullConfig, CancellationToken ct = default);
    ValueTask ResetToDefaultsAsync(CancellationToken ct = default);
    IDisposable OnConfigChanged<T>(Action<T> onChanged) where T : class, new();
}
