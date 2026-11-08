using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Database;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ImmersiveReading.Core.Tests.Database;

public sealed class MigrationRunnerTests
{
    private record TestMigration(string ModuleId, int Version, string Name, string Sql) : IDbMigration;

    [Fact]
    public void AppliesPendingMigrationsInOrderAndRecordsInTable()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var migrations = new IDbMigration[]
        {
            new TestMigration("test_mod", 1, "CreateT1", "CREATE TABLE t1 (id INT PRIMARY KEY);"),
            new TestMigration("test_mod", 2, "CreateT2", "CREATE TABLE t2 (name TEXT);")
        };

        MigrationRunner.ApplyMigrations(conn, "test_mod", migrations);

        // Verify tables exist
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE module_id = 'test_mod';";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(2, count);
        }

        // Running again is idempotent
        MigrationRunner.ApplyMigrations(conn, "test_mod", migrations);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE module_id = 'test_mod';";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(2, count);
        }
    }

    [Fact]
    public void IgnoresMigrationsForOtherModules()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var migrations = new IDbMigration[]
        {
            new TestMigration("module_a", 1, "A1", "CREATE TABLE table_a (id INT);"),
            new TestMigration("module_b", 1, "B1", "CREATE TABLE table_b (id INT);")
        };

        MigrationRunner.ApplyMigrations(conn, "module_a", migrations);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE module_id = 'module_a';";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(1, count);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE module_id = 'module_b';";
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(0, count);
        }
    }
}

