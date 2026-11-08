using ImmersiveReading.Dictionaries;
using ImmersiveReading.Dictionaries.Abstractions;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed record DictionaryPreset(
    string Id,
    string Title,
    string Description,
    string DownloadUrl,
    double ApproxSizeMb,
    int DefaultPriority
);

public sealed class DictionaryRpcModule : IRpcModule
{
    private readonly DictionaryDatabase? _dictDb;
    private readonly DictionaryImporterRegistry _importerRegistry;
    private readonly IRpcEventSink? _events;

    public static readonly DictionaryPreset[] Presets =
    [
        new(
            "jmdict_english",
            "JMdict (English)",
            "Comprehensive Japanese-English general vocabulary dictionary with word classes, meanings, and tags (EDRDG CC BY-SA 4.0).",
            "https://github.com/themoeway/yomitan-bikun/releases/latest/download/JMdict_english.zip",
            18.5,
            10),
        new(
            "kanjidic2",
            "KANJIDIC2 (English)",
            "Kanji dictionary providing Onyomi, Kunyomi, meanings, radicals, and stroke numbers for all Joyo & Jinmeiyo kanji (EDRDG).",
            "https://github.com/themoeway/yomitan-bikun/releases/latest/download/Kanjidic_english.zip",
            2.4,
            20),
        new(
            "jmnedict",
            "JMnedict (Names)",
            "Japanese named entities dictionary covering persons, places, surnames, company and product names.",
            "https://github.com/themoeway/yomitan-bikun/releases/latest/download/JMnedict.zip",
            12.0,
            30),
        new(
            "kireicake_accents",
            "KireiCake (Pitch Accents)",
            "Detailed pitch accent contours and downstep indicators for standard Japanese vocabulary.",
            "https://github.com/themoeway/yomitan-bikun/releases/latest/download/accents.zip",
            4.2,
            40)
    ];

    public DictionaryRpcModule(
        DictionaryDatabase? dictDb = null,
        DictionaryImporterRegistry? importerRegistry = null,
        IRpcEventSink? events = null)
    {
        _dictDb = dictDb;
        _events = events;
        _importerRegistry = importerRegistry ?? (dictDb != null
            ? new DictionaryImporterRegistry([new YomitanSqliteImporter(dictDb), new KanjidicImporter(dictDb)])
            : new DictionaryImporterRegistry());
    }

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("dictionaries.list", (req, _) => Task.FromResult<object?>(ListDictionaries()));
            yield return ("dictionaries.getPresets", (req, _) => Task.FromResult<object?>(GetPresets()));
            yield return ("dictionaries.importYomitan", (req, ct) => Task.FromResult<object?>(ImportYomitan(req, ct)));
            yield return ("dictionaries.import", (req, ct) => Task.FromResult<object?>(ImportGeneric(req, ct)));
            yield return ("dictionaries.downloadAndInstall", DownloadAndInstallAsync);
        }
    }

    private static object? GetPresets() => new { presets = Presets };

    private object? ListDictionaries()
    {
        if (_dictDb is null) return new { dictionaries = Array.Empty<object>() };
        var dicts = new List<object>();
        using var conn = _dictDb.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dictionary_id, title, revision, language, priority_order, enabled, imported_at_utc FROM dictionary_sources ORDER BY priority_order ASC;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dicts.Add(new
            {
                dictionaryId = r.GetString(0),
                title = r.GetString(1),
                revision = r.GetString(2),
                language = r.GetString(3),
                priorityOrder = r.GetInt32(4),
                enabled = r.GetInt32(5) == 1,
                importedAtUtc = r.GetString(6)
            });
        }
        return new { dictionaries = dicts };
    }

    private object? ImportYomitan(RpcRequest req, CancellationToken ct)
    {
        if (_dictDb is null) throw new InvalidOperationException("Dictionary database not available.");
        string zipPath = req.RequiredString("zipPath");
        int priority = req.OptionalInt("priorityOrder") ?? 100;
        var importer = new YomitanSqliteImporter(_dictDb);
        return importer.ImportZip(zipPath, priority, ct);
    }

    private object? ImportGeneric(RpcRequest req, CancellationToken ct)
    {
        string filePath = req.RequiredString("filePath");
        int priority = req.OptionalInt("priorityOrder") ?? 100;
        var importer = _importerRegistry.Resolve(filePath);
        return importer.Import(filePath, priority, ct);
    }

    private async Task<object?> DownloadAndInstallAsync(RpcRequest req, CancellationToken ct)
    {
        if (_dictDb is null) throw new InvalidOperationException("Dictionary database not available.");

        string? presetId = req.OptionalString("presetId");
        string? url = req.OptionalString("url");
        int priority = req.OptionalInt("priorityOrder") ?? 100;

        string title = "Dictionary";
        if (!string.IsNullOrWhiteSpace(presetId))
        {
            var preset = Presets.FirstOrDefault(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown dictionary preset '{presetId}'.");
            url = preset.DownloadUrl;
            title = preset.Title;
            if (req.OptionalInt("priorityOrder") == null)
            {
                priority = preset.DefaultPriority;
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Either 'presetId' or 'url' must be provided.");
        }

        string downloadDir = Path.Combine(AppContext.BaseDirectory, ".library-data", "dictionaries");
        Directory.CreateDirectory(downloadDir);

        string fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"{presetId ?? "custom"}.zip";
        }
        string targetZipPath = Path.Combine(downloadDir, fileName);

        _events?.Emit("dictionaries.downloadProgress", new { status = "downloading", title, url });

        using (var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fileStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await contentStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        }

        _events?.Emit("dictionaries.downloadProgress", new { status = "importing", title, targetZipPath });

        var importer = new YomitanSqliteImporter(_dictDb);
        var result = importer.ImportZip(targetZipPath, priority, ct);

        _events?.Emit("dictionaries.downloadProgress", new { status = "completed", title, result });

        return new
        {
            ok = true,
            dictionaryId = result.DictionaryId,
            title = result.Title,
            revision = result.Revision,
            termsCount = result.TermsCount,
            metaCount = result.MetaCount,
            kanjiCount = result.KanjiCount,
            filePath = targetZipPath
        };
    }
}
