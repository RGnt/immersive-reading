namespace ImmersiveReading.App.Plugins;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Plugins;
using Microsoft.Data.Sqlite;

public sealed class PluginDatabaseContext : IPluginDatabaseContext
{
    public string DatabaseFilePath { get; }
    public string ConnectionString { get; }

    public PluginDatabaseContext(string pluginDataDirectory)
    {
        Directory.CreateDirectory(pluginDataDirectory);
        DatabaseFilePath = Path.Combine(pluginDataDirectory, "plugin.db");
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public DbConnection CreateConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragmaCmd.ExecuteNonQuery();

        return conn;
    }

    public async ValueTask MigrateAsync(IEnumerable<PluginMigration> migrations, CancellationToken ct = default)
    {
        await using var conn = (SqliteConnection)CreateConnection();

        await using (var initCmd = conn.CreateCommand())
        {
            initCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS __plugin_migrations (
                    version INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    applied_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """;
            await initCmd.ExecuteNonQueryAsync(ct);
        }

        var appliedVersions = new HashSet<int>();
        await using (var queryCmd = conn.CreateCommand())
        {
            queryCmd.CommandText = "SELECT version FROM __plugin_migrations;";
            await using var reader = await queryCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                appliedVersions.Add(reader.GetInt32(0));
            }
        }

        foreach (var migration in migrations.OrderBy(m => m.Version))
        {
            if (appliedVersions.Contains(migration.Version))
                continue;

            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using (var sqlCmd = conn.CreateCommand())
                {
                    sqlCmd.Transaction = (SqliteTransaction)tx;
                    sqlCmd.CommandText = migration.UpSql;
                    await sqlCmd.ExecuteNonQueryAsync(ct);
                }

                await using (var recordCmd = conn.CreateCommand())
                {
                    recordCmd.Transaction = (SqliteTransaction)tx;
                    recordCmd.CommandText = "INSERT INTO __plugin_migrations (version, name) VALUES ($v, $n);";
                    recordCmd.Parameters.AddWithValue("$v", migration.Version);
                    recordCmd.Parameters.AddWithValue("$n", migration.Name);
                    await recordCmd.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
    }

    public async ValueTask MigrateFromEmbeddedResourcesAsync(
        Assembly pluginAssembly,
        string resourceNamespacePrefix,
        CancellationToken ct = default)
    {
        var prefix = resourceNamespacePrefix.EndsWith('.')
            ? resourceNamespacePrefix
            : resourceNamespacePrefix + ".";

        var resources = pluginAssembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var migrations = new List<PluginMigration>(resources.Count);

        foreach (var resName in resources)
        {
            var fileName = resName[prefix.Length..];
            var underscoreIdx = fileName.IndexOf('_');
            if (underscoreIdx <= 0 || !int.TryParse(fileName[..underscoreIdx], out var version))
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{resName}' must start with a numeric prefix like '001_name.sql'.");
            }

            await using var stream = pluginAssembly.GetManifestResourceStream(resName)!;
            using var sr = new StreamReader(stream);
            var sql = await sr.ReadToEndAsync(ct);

            migrations.Add(new PluginMigration(version, fileName, sql));
        }

        await MigrateAsync(migrations, ct);
    }
}
