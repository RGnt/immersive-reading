using System.Data.Common;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Database;
using ImmersiveReading.Dictionaries.Schema;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries;

public sealed class DictionaryDatabase : IDbConnectionFactory, IDisposable
{
    private readonly string _connectionString;

    public string DbPath { get; }

    public DictionaryDatabase(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ConnectionString;

        InitializeDatabase();
    }

    public SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            """;
        pragma.ExecuteNonQuery();
        return conn;
    }

    DbConnection IDbConnectionFactory.OpenConnection() => CreateConnection();

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        MigrationRunner.ApplyMigrations(conn, DictionaryMigrations.ModuleId, DictionaryMigrations.All);
    }

    public void Dispose()
    {
    }
}
