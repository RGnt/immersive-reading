using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.StudyPack;

namespace ImmersiveReading.Library;

/// <summary>
/// Facade over the study-pack components: publication reading, snapshot leases,
/// overview building and the Python build runner.
/// </summary>
public sealed class PythonStudyPackBridge : IStudyPackCatalog
{
    private readonly string _outputRoot;
    private readonly IStudyPackBuilder _builder;

    public string OutputRoot => _outputRoot;

    public PythonStudyPackBridge(string outputRoot, string? extractorRoot = null, IStudyPackBuilder? builder = null)
    {
        _outputRoot = Path.GetFullPath(outputRoot);
        _builder = builder ?? new PythonBuildRunner(_outputRoot, extractorRoot);
    }

    public (string? Generation, string? ManifestPath, JsonDocument? ManifestDoc) TryReadActivePublication()
        => PublicationReader.TryReadActivePublication(_outputRoot);

    public StudyPackLease? AcquireSnapshotLease(string? generation = null)
        => SnapshotLeaseManager.AcquireSnapshotLease(_outputRoot, generation);

    public StudyPackBookOverview GetBookStudyPack(string bookId)
        => StudyPackOverviewBuilder.GetBookStudyPack(_outputRoot, bookId);

    public Task<StudyPackBuildResult> BuildStudyPackAsync(
        string bookId,
        string bookFolderPath,
        bool readerOnly = false,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
        => _builder.BuildStudyPackAsync(bookId, bookFolderPath, readerOnly, onProgressLine, ct);
}
