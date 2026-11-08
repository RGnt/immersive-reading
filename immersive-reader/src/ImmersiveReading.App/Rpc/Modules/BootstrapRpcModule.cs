using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class BootstrapRpcModule : IRpcModule
{
    private readonly LibraryManager _library;
    private readonly IConfigurationManager? _configManager;
    private readonly string? _workspaceBooksDir;
    private readonly DictionaryDatabase? _dictDb;
    private bool _bootstrappedWorkspaceBooks;

    public BootstrapRpcModule(
        LibraryManager library,
        IConfigurationManager? configManager = null,
        string? workspaceBooksDir = null,
        DictionaryDatabase? dictDb = null)
    {
        _library = library;
        _configManager = configManager;
        _workspaceBooksDir = workspaceBooksDir;
        _dictDb = dictDb;
    }

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("library.getBootstrapState", GetBootstrapStateAsync);
        }
    }

    private async Task<object?> GetBootstrapStateAsync(RpcRequest req, CancellationToken ct)
    {
        await EnsureInitialStarterLibraryAsync(ct).ConfigureAwait(false);
        var folders = _library.GetFolderTree();
        var tags = _library.ListTags();
        var customFields = _library.ListCustomFieldDefinitions();
        var initialPage = _library.ListBooks(new BookListQuery(Limit: 100));
        var config = _configManager?.GetConfig() ?? AppConfig.CreateDefault();
        string defaultScanDir = !string.IsNullOrWhiteSpace(config.Directories.DefaultScanDirectory)
            ? config.Directories.DefaultScanDirectory
            : (_workspaceBooksDir ?? "");
        return new
        {
            folders,
            tags,
            customFields,
            initialPage,
            defaultScanDir,
            config
        };
    }

    private async Task EnsureInitialStarterLibraryAsync(CancellationToken ct)
    {
        if (_bootstrappedWorkspaceBooks) return;
        _bootstrappedWorkspaceBooks = true;

        EnsureInitialStarterDictionaries(ct);

        var existing = _library.ListBooks(new BookListQuery(Limit: 1));
        var existingFolders = _library.GetFolderTree();
        if (existing.TotalCount > 0 || existingFolders.Count > 0) return;

        // Seed starter folder structure: Japanese -> Shounen / Seinen / Light Novels
        var jaRoot = _library.SaveFolder("fld-japanese", null, "Japanese", "collection", "Japanese Ebook Collection", sortOrder: 1);
        _library.SaveFolder("fld-shounen", jaRoot.FolderId, "Shounen", "category", sortOrder: 1);
        _library.SaveFolder("fld-seinen", jaRoot.FolderId, "Seinen", "category", sortOrder: 2);
        var lnFolder = _library.SaveFolder("fld-light-novels", jaRoot.FolderId, "Light Novels & Stories", "category", sortOrder: 3);

        if (!string.IsNullOrWhiteSpace(_workspaceBooksDir) && Directory.Exists(_workspaceBooksDir))
        {
            await _library.ScanDirectoryAsync(_workspaceBooksDir, targetFolderId: lnFolder.FolderId, recursive: true, ct: ct)
                .ConfigureAwait(false);
        }
    }

    private void EnsureInitialStarterDictionaries(CancellationToken ct)
    {
        if (_dictDb is null || string.IsNullOrWhiteSpace(_workspaceBooksDir)) return;

        try
        {
            long termCount = 0;
            long kanjiCount = 0;
            using (var conn = _dictDb.CreateConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM dictionary_sources;";
                    termCount = Convert.ToInt64(cmd.ExecuteScalar());
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM dictionary_kanji;";
                    kanjiCount = Convert.ToInt64(cmd.ExecuteScalar());
                }
            }

            if (termCount == 0 && Directory.Exists(_workspaceBooksDir))
            {
                var csvFiles = Directory.GetFiles(_workspaceBooksDir, "*.csv", SearchOption.AllDirectories);
                if (csvFiles.Length > 0)
                {
                    var importer = new YomitanSqliteImporter(_dictDb);
                    importer.ImportJmdictDeckCsvs(csvFiles);
                }
            }

            if (kanjiCount == 0)
            {
                string? extractorDir = Path.GetDirectoryName(_workspaceBooksDir);
                if (!string.IsNullOrEmpty(extractorDir))
                {
                    string dataDir = Path.Combine(extractorDir, "data");
                    string kanjidicGz = Path.Combine(dataDir, "kanjidic2.xml.gz");
                    string kaCsv = Path.Combine(dataDir, "kanji-alive", "ka_data.csv");
                    if (File.Exists(kanjidicGz))
                    {
                        var kImporter = new KanjidicImporter(_dictDb);
                        kImporter.Import(kanjidicGz, File.Exists(kaCsv) ? kaCsv : null, kanjivgZipPath: null, ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StarterDictionaries] {ex.GetType().Name}: {ex.Message}");
        }
    }
}

