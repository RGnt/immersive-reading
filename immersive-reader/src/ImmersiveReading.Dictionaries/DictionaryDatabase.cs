using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries;

public sealed class DictionaryDatabase : IDisposable
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
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
    }

    private const string SchemaSql = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;

        CREATE TABLE IF NOT EXISTS dictionary_sources (
            dictionary_id       TEXT PRIMARY KEY,
            title               TEXT NOT NULL,
            revision            TEXT NOT NULL,
            language            TEXT NOT NULL DEFAULT 'ja',
            target_language     TEXT NOT NULL DEFAULT 'en',
            priority_order      INTEGER NOT NULL,
            enabled             INTEGER NOT NULL DEFAULT 1,
            author              TEXT,
            url                 TEXT,
            attribution         TEXT,
            notices_json        TEXT NOT NULL DEFAULT '{}',
            bank_counts_json    TEXT NOT NULL DEFAULT '{}',
            imported_at_utc     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS dictionary_terms (
            term_id             INTEGER PRIMARY KEY AUTOINCREMENT,
            dictionary_id       TEXT NOT NULL REFERENCES dictionary_sources(dictionary_id) ON DELETE CASCADE,
            expression          TEXT NOT NULL,
            reading             TEXT NOT NULL,
            reading_norm        TEXT NOT NULL,
            definition_tags     TEXT NOT NULL,
            rules               TEXT NOT NULL,
            score               REAL NOT NULL,
            glossary_json       TEXT NOT NULL,
            glossary_text       TEXT NOT NULL,
            sequence            INTEGER NOT NULL,
            term_tags           TEXT NOT NULL,
            bank_name           TEXT NOT NULL,
            bank_row            INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_terms_expr ON dictionary_terms(expression);
        CREATE INDEX IF NOT EXISTS idx_terms_read_norm ON dictionary_terms(reading_norm);
        CREATE INDEX IF NOT EXISTS idx_terms_expr_read ON dictionary_terms(expression, reading_norm);

        CREATE TABLE IF NOT EXISTS dictionary_term_meta (
            meta_id             INTEGER PRIMARY KEY AUTOINCREMENT,
            dictionary_id       TEXT NOT NULL REFERENCES dictionary_sources(dictionary_id) ON DELETE CASCADE,
            expression          TEXT NOT NULL,
            reading_norm        TEXT,
            mode                TEXT NOT NULL,
            data_json           TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_term_meta_lookup ON dictionary_term_meta(expression, reading_norm);

        CREATE TABLE IF NOT EXISTS dictionary_kanji (
            character           TEXT PRIMARY KEY,
            on_readings_json    TEXT NOT NULL,
            kun_readings_json   TEXT NOT NULL,
            primary_meanings    TEXT NOT NULL,
            primary_source      TEXT NOT NULL,
            kanjidic_meanings   TEXT NOT NULL,
            stroke_count        INTEGER,
            grade               TEXT,
            radical             TEXT,
            frequency_json      TEXT,
            kanjivg_strokes_json TEXT
        );
        """;
}

