using System.Data.Common;
using ImmersiveReading.Core.Abstractions;

namespace ImmersiveReading.Core.Database;

public static class MigrationRunner
{
    public static void ApplyMigrations(DbConnection connection, string moduleId, IEnumerable<IDbMigration> migrations)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(migrations);

        // 1. Ensure schema_migrations table exists
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    module_id TEXT NOT NULL,
                    version INTEGER NOT NULL,
                    name TEXT NOT NULL DEFAULT '',
                    applied_at_utc TEXT NOT NULL,
                    PRIMARY KEY (module_id, version)
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // 2. Query applied versions
        var applied = new HashSet<int>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_migrations WHERE module_id = $mod ORDER BY version ASC;";
            var pMod = cmd.CreateParameter();
            pMod.ParameterName = "$mod";
            pMod.Value = moduleId;
            cmd.Parameters.Add(pMod);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        // 3. Filter and sort pending migrations
        var pending = migrations
            .Where(m => string.Equals(m.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) && !applied.Contains(m.Version))
            .OrderBy(m => m.Version)
            .ToList();

        if (pending.Count == 0) return;

        // 4. Apply each migration in a transaction
        foreach (var migration in pending)
        {
            using var tx = connection.BeginTransaction();
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = migration.Sql;
                    cmd.ExecuteNonQuery();
                }

                using (var recordCmd = connection.CreateCommand())
                {
                    recordCmd.Transaction = tx;
                    recordCmd.CommandText = """
                        INSERT INTO schema_migrations (module_id, version, name, applied_at_utc)
                        VALUES ($mod, $ver, $name, $now);
                        """;
                    var pMod = recordCmd.CreateParameter();
                    pMod.ParameterName = "$mod";
                    pMod.Value = migration.ModuleId;
                    recordCmd.Parameters.Add(pMod);

                    var pVer = recordCmd.CreateParameter();
                    pVer.ParameterName = "$ver";
                    pVer.Value = migration.Version;
                    recordCmd.Parameters.Add(pVer);

                    var pName = recordCmd.CreateParameter();
                    pName.ParameterName = "$name";
                    pName.Value = migration.Name;
                    recordCmd.Parameters.Add(pName);

                    var pNow = recordCmd.CreateParameter();
                    pNow.ParameterName = "$now";
                    pNow.Value = DateTime.UtcNow.ToString("O");
                    recordCmd.Parameters.Add(pNow);

                    recordCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}

