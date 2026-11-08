using ImmersiveReading.Dictionaries;
using ImmersiveReading.Dictionaries.Abstractions;
using Xunit;

namespace ImmersiveReading.Dictionaries.Tests;

public sealed class DictionaryImporterStrategyTests
{
    private sealed class StubImporter : IDictionaryImporter
    {
        public string FormatName => "stub";
        public bool CanHandle(string filePath) => filePath.EndsWith(".stub", StringComparison.OrdinalIgnoreCase);
        public DictionaryImportResult Import(string filePath, int priorityOrder = 100, CancellationToken ct = default)
            => new(true, "Stub Dictionary", 42, 10, "Success");
    }

    [Fact]
    public void Registry_ResolvesMatchingImporter()
    {
        var stub = new StubImporter();
        var registry = new DictionaryImporterRegistry([stub]);

        var resolved = registry.Resolve("test.stub");
        Assert.Same(stub, resolved);

        var result = resolved.Import("test.stub");
        Assert.True(result.Success);
        Assert.Equal(42, result.TermsCount);
        Assert.Equal(10, result.KanjiCount);
    }

    [Fact]
    public void Registry_ThrowsNotSupported_WhenNoImporterMatches()
    {
        var registry = new DictionaryImporterRegistry();
        Assert.Throws<NotSupportedException>(() => registry.Resolve("unknown.xyz"));
    }

    [Fact]
    public void YomitanImporter_CanHandleZip()
    {
        var db = new DictionaryDatabase(":memory:");
        var importer = new YomitanSqliteImporter(db);

        Assert.True(importer.CanHandle("JMdict_english.zip"));
        Assert.False(importer.CanHandle("kanjidic2.xml.gz"));
    }

    [Fact]
    public void KanjidicImporter_CanHandleXmlAndGz()
    {
        var db = new DictionaryDatabase(":memory:");
        var importer = new KanjidicImporter(db);

        Assert.True(importer.CanHandle("kanjidic2.xml.gz"));
        Assert.True(importer.CanHandle("kanjidic2.xml"));
        Assert.False(importer.CanHandle("dictionary.zip"));
    }
}

