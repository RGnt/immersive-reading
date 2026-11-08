using System.Text.Json;
using ImmersiveReading.Core.Config;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library;

public sealed class ConfigurationManager : IConfigurationManager
{
    private readonly LibraryDatabase _db;
    private AppConfig _currentConfig;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public event EventHandler<AppConfig>? ConfigChanged;

    public AppConfig CurrentConfig
    {
        get
        {
            lock (_lock)
            {
                return _currentConfig;
            }
        }
    }

    public ConfigurationManager(LibraryDatabase db, string? initialDefaultScanDir = null)
    {
        _db = db;
        _currentConfig = LoadOrSeedConfig(initialDefaultScanDir);
    }

    public AppConfig GetConfig() => CurrentConfig;

    public Task<AppConfig> UpdateConfigAsync(AppConfig newConfig, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var merged = _currentConfig with
            {
                Directories = newConfig.Directories ?? _currentConfig.Directories,
                Appearance = newConfig.Appearance ?? _currentConfig.Appearance,
                Reader = newConfig.Reader ?? _currentConfig.Reader,
                Llm = newConfig.Llm ?? _currentConfig.Llm,
                Dictionaries = newConfig.Dictionaries ?? _currentConfig.Dictionaries,
                Library = newConfig.Library ?? _currentConfig.Library
            };

            using var conn = _db.OpenConnection();
            using var tx = conn.BeginTransaction();

            SaveCategory(conn, tx, "directories", merged.Directories);
            SaveCategory(conn, tx, "appearance", merged.Appearance);
            SaveCategory(conn, tx, "reader", merged.Reader);
            SaveCategory(conn, tx, "llm", merged.Llm);
            SaveCategory(conn, tx, "dictionaries", merged.Dictionaries);
            SaveCategory(conn, tx, "library", merged.Library);

            tx.Commit();

            _currentConfig = merged;
        }

        ConfigChanged?.Invoke(this, _currentConfig);
        return Task.FromResult(_currentConfig);
    }

    public Task<AppConfig> UpdateCategoryJsonAsync(string category, string categoryJson, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var conn = _db.OpenConnection();
            using var tx = conn.BeginTransaction();

            string normCategory = category.Trim().ToLowerInvariant();
            var current = _currentConfig;

            AppConfig updated;
            try
            {
                updated = normCategory switch
                {
                    "directories" => current with
                    {
                        Directories = JsonSerializer.Deserialize<DirectorySettings>(categoryJson, JsonOpts) ?? current.Directories
                    },
                    "appearance" => current with
                    {
                        Appearance = JsonSerializer.Deserialize<AppearanceSettings>(categoryJson, JsonOpts) ?? current.Appearance
                    },
                    "reader" => current with
                    {
                        Reader = JsonSerializer.Deserialize<ReaderSettings>(categoryJson, JsonOpts) ?? current.Reader
                    },
                    "llm" => current with
                    {
                        Llm = JsonSerializer.Deserialize<LlmSettings>(categoryJson, JsonOpts) ?? current.Llm
                    },
                    "dictionaries" => current with
                    {
                        Dictionaries = JsonSerializer.Deserialize<DictionarySettings>(categoryJson, JsonOpts) ?? current.Dictionaries
                    },
                    "library" => current with
                    {
                        Library = JsonSerializer.Deserialize<LibrarySettings>(categoryJson, JsonOpts) ?? current.Library
                    },
                    _ => throw new ArgumentException($"Unknown configuration category '{category}'.")
                };
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"Invalid JSON payload for category '{category}': {ex.Message}", ex);
            }

            SaveCategory(conn, tx, normCategory, normCategory switch
            {
                "directories" => (object)updated.Directories,
                "appearance" => updated.Appearance,
                "reader" => updated.Reader,
                "llm" => updated.Llm,
                "dictionaries" => updated.Dictionaries,
                "library" => updated.Library,
                _ => throw new InvalidOperationException()
            });

            tx.Commit();
            _currentConfig = updated;
        }

        ConfigChanged?.Invoke(this, _currentConfig);
        return Task.FromResult(_currentConfig);
    }

    public Task<AppConfig> ResetDefaultsAsync(string? category = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            using var conn = _db.OpenConnection();
            using var tx = conn.BeginTransaction();

            string? normCategory = category?.Trim().ToLowerInvariant();
            var defaults = AppConfig.CreateDefault();
            var current = _currentConfig;

            if (string.IsNullOrEmpty(normCategory) || normCategory == "all")
            {
                SaveCategory(conn, tx, "directories", defaults.Directories);
                SaveCategory(conn, tx, "appearance", defaults.Appearance);
                SaveCategory(conn, tx, "reader", defaults.Reader);
                SaveCategory(conn, tx, "llm", defaults.Llm);
                SaveCategory(conn, tx, "dictionaries", defaults.Dictionaries);
                SaveCategory(conn, tx, "library", defaults.Library);
                _currentConfig = defaults;
            }
            else
            {
                AppConfig updated = normCategory switch
                {
                    "directories" => current with { Directories = defaults.Directories },
                    "appearance" => current with { Appearance = defaults.Appearance },
                    "reader" => current with { Reader = defaults.Reader },
                    "llm" => current with { Llm = defaults.Llm },
                    "dictionaries" => current with { Dictionaries = defaults.Dictionaries },
                    "library" => current with { Library = defaults.Library },
                    _ => throw new ArgumentException($"Unknown configuration category '{category}'.")
                };

                SaveCategory(conn, tx, normCategory, normCategory switch
                {
                    "directories" => (object)defaults.Directories,
                    "appearance" => defaults.Appearance,
                    "reader" => defaults.Reader,
                    "llm" => defaults.Llm,
                    "dictionaries" => defaults.Dictionaries,
                    "library" => defaults.Library,
                    _ => throw new InvalidOperationException()
                });

                _currentConfig = updated;
            }

            tx.Commit();
        }

        ConfigChanged?.Invoke(this, _currentConfig);
        return Task.FromResult(_currentConfig);
    }

    private AppConfig LoadOrSeedConfig(string? initialDefaultScanDir)
    {
        using var conn = _db.OpenConnection();
        var defaults = AppConfig.CreateDefault();
        if (!string.IsNullOrWhiteSpace(initialDefaultScanDir))
        {
            defaults = defaults with
            {
                Directories = defaults.Directories with { DefaultScanDirectory = initialDefaultScanDir }
            };
        }

        var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT setting_key, value_json FROM app_settings;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                string json = reader.GetString(1);
                categories[key] = json;
            }
        }

        using var tx = conn.BeginTransaction();
        bool changed = false;

        DirectorySettings dirSettings = defaults.Directories;
        if (categories.TryGetValue("directories", out var dirJson))
        {
            dirSettings = JsonSerializer.Deserialize<DirectorySettings>(dirJson, JsonOpts) ?? defaults.Directories;
        }
        else
        {
            SaveCategory(conn, tx, "directories", dirSettings);
            changed = true;
        }

        AppearanceSettings appSettings = defaults.Appearance;
        if (categories.TryGetValue("appearance", out var appJson))
        {
            appSettings = JsonSerializer.Deserialize<AppearanceSettings>(appJson, JsonOpts) ?? defaults.Appearance;
        }
        else
        {
            SaveCategory(conn, tx, "appearance", appSettings);
            changed = true;
        }

        ReaderSettings rdrSettings = defaults.Reader;
        if (categories.TryGetValue("reader", out var rdrJson))
        {
            rdrSettings = JsonSerializer.Deserialize<ReaderSettings>(rdrJson, JsonOpts) ?? defaults.Reader;
        }
        else
        {
            SaveCategory(conn, tx, "reader", rdrSettings);
            changed = true;
        }

        LlmSettings llmSettings = defaults.Llm;
        if (categories.TryGetValue("llm", out var llmJson))
        {
            llmSettings = JsonSerializer.Deserialize<LlmSettings>(llmJson, JsonOpts) ?? defaults.Llm;
        }
        else
        {
            SaveCategory(conn, tx, "llm", llmSettings);
            changed = true;
        }

        DictionarySettings dictSettings = defaults.Dictionaries;
        if (categories.TryGetValue("dictionaries", out var dictJson))
        {
            dictSettings = JsonSerializer.Deserialize<DictionarySettings>(dictJson, JsonOpts) ?? defaults.Dictionaries;
        }
        else
        {
            SaveCategory(conn, tx, "dictionaries", dictSettings);
            changed = true;
        }

        LibrarySettings libSettings = defaults.Library;
        if (categories.TryGetValue("library", out var libJson))
        {
            libSettings = JsonSerializer.Deserialize<LibrarySettings>(libJson, JsonOpts) ?? defaults.Library;
        }
        else
        {
            SaveCategory(conn, tx, "library", libSettings);
            changed = true;
        }

        if (changed)
        {
            tx.Commit();
        }

        return new AppConfig(dirSettings, appSettings, rdrSettings, llmSettings, dictSettings, libSettings);
    }

    private static void SaveCategory(SqliteConnection conn, SqliteTransaction tx, string category, object data)
    {
        string json = JsonSerializer.Serialize(data, JsonOpts);
        string now = DateTime.UtcNow.ToString("O");
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO app_settings (setting_key, category, value_json, updated_at_utc)
            VALUES ($key, $cat, $json, $now)
            ON CONFLICT(setting_key) DO UPDATE SET
                value_json = excluded.value_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$key", category);
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.Parameters.AddWithValue("$json", json);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }
}
