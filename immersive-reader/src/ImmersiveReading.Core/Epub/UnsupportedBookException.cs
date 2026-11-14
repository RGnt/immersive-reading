namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Thrown when a file is a valid EPUB but of a kind this reader does not support (currently: image-only
/// books such as comics). It is a deliberate refusal, not a parsing failure.
/// </summary>
public sealed class UnsupportedBookException(string fileName, BookContentProfile profile)
    : InvalidOperationException(BuildMessage(fileName, profile))
{
    public string FileName { get; } = fileName;
    public BookContentProfile Profile { get; } = profile;

    /// <summary>The same sentence used as a scan warning.</summary>
    public static string BuildMessage(string fileName, BookContentProfile profile) =>
        $"{fileName}: image-only book skipped ({profile.Describe()}). " +
        "Comics and scanned books are not supported yet; they belong in a comic format (.cbz or similar) once that is supported.";
}
