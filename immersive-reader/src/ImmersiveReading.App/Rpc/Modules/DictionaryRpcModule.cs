using ImmersiveReading.Dictionaries;
using ImmersiveReading.Dictionaries.Abstractions;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class DictionaryRpcModule : IRpcModule
{
    private readonly DictionaryDatabase? _dictDb;
    private readonly DictionaryImporterRegistry _importerRegistry;

    public DictionaryRpcModule(DictionaryDatabase? dictDb = null, DictionaryImporterRegistry? importerRegistry = null)
    {
        _dictDb = dictDb;
        _importerRegistry = importerRegistry ?? (dictDb != null
            ? new DictionaryImporterRegistry([new YomitanSqliteImporter(dictDb), new KanjidicImporter(dictDb)])
            : new DictionaryImporterRegistry());
    }

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("dictionaries.list", (req, _) => Task.FromResult<object?>(ListDictionaries()));
            yield return ("dictionaries.importYomitan", (req, ct) => Task.FromResult<object?>(ImportYomitan(req, ct)));
            yield return ("dictionaries.import", (req, ct) => Task.FromResult<object?>(ImportGeneric(req, ct)));
        }
    }

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
}
