namespace ImmersiveReading.Core.Library;

public sealed record StudyPackPdfArtifact(
    string FileName,
    string Title,
    string Kind,          // "master" | "chapter-kanji" | "chapter-vocabulary"
    string FullPath,
    string AppSchemeUrl,  // e.g. "ir-app://studypack/pdf/book-.../kanji-master.pdf"
    long SizeBytes
);

public sealed record VocabularySetWordItem(
    string CardId,
    string Word,
    string Reading,
    string ChapterId,
    int StudyNumber
);

public sealed record VocabularyLearningSet(
    string Id,
    int Number,
    int Size,
    IReadOnlyList<VocabularySetWordItem> Words
);

public sealed record StudyPackBookOverview(
    string BookId,
    bool Available,
    string? Generation,
    string? ActiveManifestPath,
    IReadOnlyList<StudyPackPdfArtifact> MasterPdfs,
    IReadOnlyList<StudyPackPdfArtifact> ChapterPdfs,
    IReadOnlyList<VocabularyLearningSet> VocabularySets,
    int TotalVocabularyInSets,
    bool HasHtmlCompanion,
    string? HtmlEntryPath
);

public sealed record StudyPackBuildResult(
    bool Success,
    int ExitCode,
    string OutputLog,
    StudyPackBookOverview? StudyPack
);

public sealed record StudyPackOptions(
    string? DeckCsvPath = null,
    bool IncludeA4Pdf = true,
    bool ReaderOnly = false
);

