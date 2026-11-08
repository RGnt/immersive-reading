namespace ImmersiveReading.Dictionaries.Abstractions;

public sealed record DictionaryImportResult(
    bool Success,
    string DictionaryTitle,
    int TermsCount,
    int KanjiCount,
    string Message = ""
);

/// <summary>
/// Strategy interface for dictionary file format importers.
/// </summary>
public interface IDictionaryImporter
{
    string FormatName { get; }

    bool CanHandle(string filePath);

    DictionaryImportResult Import(
        string filePath,
        int priorityOrder = 100,
        CancellationToken ct = default);
}

