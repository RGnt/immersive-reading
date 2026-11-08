namespace ImmersiveReading.Core.Plugins;

using System.Collections.Generic;
using System.Data.Common;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

public record PluginMigration(int Version, string Name, string UpSql);

public interface IPluginDatabaseContext
{
    string DatabaseFilePath { get; }
    string ConnectionString { get; }
    DbConnection CreateConnection();
    ValueTask MigrateAsync(IEnumerable<PluginMigration> migrations, CancellationToken ct = default);
    ValueTask MigrateFromEmbeddedResourcesAsync(
        Assembly pluginAssembly,
        string resourceNamespacePrefix,
        CancellationToken ct = default);
}
