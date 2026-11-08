namespace ImmersiveReading.Core.Library;

public sealed record ScanBatchProgress(
    int ScannedFiles,
    int TotalFiles,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    string CurrentFile
);

public sealed record ScanBatchResult(
    int TotalFilesDiscovered,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    double ElapsedSeconds,
    IReadOnlyList<string> Errors
);

