namespace ImmersiveReading.Core.Library;

public sealed record ScanBatchProgress(
    int ScannedFiles,
    int TotalFiles,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    string CurrentFile,
    int SkippedFiles = 0
);

public sealed record ScanBatchResult(
    int TotalFilesDiscovered,
    int ImportedNew,
    int DeduplicatedExisting,
    int FailedFiles,
    double ElapsedSeconds,
    IReadOnlyList<string> Errors,
    /// <summary>Image-only books (comics, scanned books) that were deliberately not imported (spec 13).</summary>
    int SkippedImageOnly = 0,
    /// <summary>One human-readable warning per skipped file. Not failures.</summary>
    IReadOnlyList<string>? Warnings = null,
    /// <summary>Files skipped because the user removed them as duplicates (spec 21). One warning per file is included in <see cref="Warnings"/>.</summary>
    int SkippedRemovedDuplicates = 0
);

