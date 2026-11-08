using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class ChapterPosCache : IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();
    private bool _initialized;

    public int MaxCacheSizeMb { get; set; } = 500;

    public ChapterPosCache(string? dbPath = null, int maxCacheSizeMb = 500)
    {
        MaxCacheSizeMb = maxCacheSizeMb > 0 ? maxCacheSizeMb : 500;
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            string appData = Path.Combine(AppContext.BaseDirectory, ".library-data", "cache");
            Directory.CreateDirectory(appData);
            _dbPath = Path.Combine(appData, "pos_chapter_cache.db");
        }
        else
        {
            _dbPath = dbPath;
            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            using var conn = CreateConnection();
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS chapter_pos_cache (
                    book_id TEXT NOT NULL,
                    chapter_href TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    pos_data_json TEXT NOT NULL,
                    size_bytes INTEGER NOT NULL,
                    last_accessed_utc INTEGER NOT NULL,
                    PRIMARY KEY (book_id, chapter_href, content_hash)
                );
                CREATE INDEX IF NOT EXISTS idx_pos_cache_lru ON chapter_pos_cache (last_accessed_utc ASC);
            ";
            cmd.ExecuteNonQuery();
            _initialized = true;
        }
    }

    public SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    public string? Get(string bookId, string chapterHref, string contentHash)
    {
        EnsureInitialized();
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT pos_data_json FROM chapter_pos_cache
                    WHERE book_id = @b AND chapter_href = @ch AND content_hash = @h;
                ";
                cmd.Parameters.AddWithValue("@b", bookId);
                cmd.Parameters.AddWithValue("@ch", chapterHref);
                cmd.Parameters.AddWithValue("@h", contentHash);

                var result = cmd.ExecuteScalar() as string;
                if (result != null)
                {
                    // Touch last_accessed_utc
                    using var touchCmd = conn.CreateCommand();
                    touchCmd.CommandText = @"
                        UPDATE chapter_pos_cache
                        SET last_accessed_utc = @now
                        WHERE book_id = @b AND chapter_href = @ch AND content_hash = @h;
                    ";
                    touchCmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    touchCmd.Parameters.AddWithValue("@b", bookId);
                    touchCmd.Parameters.AddWithValue("@ch", chapterHref);
                    touchCmd.Parameters.AddWithValue("@h", contentHash);
                    touchCmd.ExecuteNonQuery();
                }

                return result;
            }
            catch
            {
                return null;
            }
        }
    }

    public void Set(string bookId, string chapterHref, string contentHash, string posDataJson)
    {
        EnsureInitialized();
        lock (_lock)
        {
            try
            {
                int sizeBytes = Encoding.UTF8.GetByteCount(posDataJson);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                using var conn = CreateConnection();
                conn.Open();
                using var tx = conn.BeginTransaction();

                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"
                    INSERT INTO chapter_pos_cache (book_id, chapter_href, content_hash, pos_data_json, size_bytes, last_accessed_utc)
                    VALUES (@b, @ch, @h, @json, @size, @now)
                    ON CONFLICT(book_id, chapter_href, content_hash) DO UPDATE SET
                        pos_data_json = excluded.pos_data_json,
                        size_bytes = excluded.size_bytes,
                        last_accessed_utc = excluded.last_accessed_utc;
                ";
                cmd.Parameters.AddWithValue("@b", bookId);
                cmd.Parameters.AddWithValue("@ch", chapterHref);
                cmd.Parameters.AddWithValue("@h", contentHash);
                cmd.Parameters.AddWithValue("@json", posDataJson);
                cmd.Parameters.AddWithValue("@size", sizeBytes);
                cmd.Parameters.AddWithValue("@now", now);
                cmd.ExecuteNonQuery();

                // Enforce cache limit (MaxCacheSizeMb)
                long maxBytes = (long)Math.Max(10, MaxCacheSizeMb) * 1024L * 1024L;

                using var sumCmd = conn.CreateCommand();
                sumCmd.Transaction = tx;
                sumCmd.CommandText = "SELECT COALESCE(SUM(size_bytes), 0) FROM chapter_pos_cache;";
                long currentBytes = Convert.ToInt64(sumCmd.ExecuteScalar());

                if (currentBytes > maxBytes)
                {
                    // LRU eviction
                    using var evictCmd = conn.CreateCommand();
                    evictCmd.Transaction = tx;
                    evictCmd.CommandText = @"
                        DELETE FROM chapter_pos_cache
                        WHERE rowid IN (
                            SELECT rowid FROM chapter_pos_cache
                            ORDER BY last_accessed_utc ASC
                            LIMIT 50
                        );
                    ";
                    evictCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch
            {
                // Silently ignore cache write failures
            }
        }
    }

    public void Clear()
    {
        EnsureInitialized();
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM chapter_pos_cache;";
                cmd.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }

    public (int Count, long TotalBytes) GetStats()
    {
        EnsureInitialized();
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM chapter_pos_cache;";
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    return (reader.GetInt32(0), reader.GetInt64(1));
                }
            }
            catch
            {
            }
            return (0, 0);
        }
    }

    public void Dispose()
    {
        // SQLite pooled connections are managed by provider
    }
}

