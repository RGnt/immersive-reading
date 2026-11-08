namespace ImmersiveReading.Core.Plugins;

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed record ActivePythonStageRegistration(
    string PluginId,
    string PluginDataDirectory,
    JsonElement PluginConfig,
    PythonStageDescriptor Descriptor);

public interface IPythonStageRegistryProvider
{
    /// <summary>
    /// Resolves all enabled stages matching the given book language (or "*"), sorted by Order ascending,
    /// and ensures their embedded Python scripts/assets are materialized on disk.
    /// </summary>
    ValueTask<IReadOnlyList<ActivePythonStageRegistration>> ResolveStagesForLanguageAsync(
        string languageCode,
        CancellationToken ct = default);
}

