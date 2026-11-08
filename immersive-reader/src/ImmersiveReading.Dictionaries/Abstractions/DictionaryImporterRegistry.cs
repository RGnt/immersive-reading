namespace ImmersiveReading.Dictionaries.Abstractions;

/// <summary>
/// Registry strategy resolver for dictionary importers.
/// </summary>
public sealed class DictionaryImporterRegistry
{
    private readonly List<IDictionaryImporter> _importers = [];

    public IReadOnlyList<IDictionaryImporter> Importers => _importers.AsReadOnly();

    public DictionaryImporterRegistry(IEnumerable<IDictionaryImporter>? initialImporters = null)
    {
        if (initialImporters != null)
        {
            _importers.AddRange(initialImporters);
        }
    }

    public void Register(IDictionaryImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        _importers.Add(importer);
    }

    public IDictionaryImporter? FindImporter(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        return _importers.FirstOrDefault(i => i.CanHandle(filePath));
    }

    public IDictionaryImporter Resolve(string filePath)
    {
        return FindImporter(filePath)
            ?? throw new NotSupportedException($"No registered dictionary importer can handle file: '{filePath}'.");
    }
}

