namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Thresholds for detecting image-only books (spec 13). Measured on a 2,223-book library: the average
/// number of visible characters per spine document has a clean gap between 60 and 100 (no book lies in
/// it), so the limit sits in the middle of the gap. The image-page ratio alone is NOT a safe signal
/// (547 ordinary books lie between 50% and 90%), hence both conditions are required.
/// </summary>
public static class ImageOnlyPolicy
{
    /// <summary>A spine document is an "image page" when it holds an image and at most this many visible characters.</summary>
    public const int ImagePageMaxChars = 50;

    /// <summary>At least this share of spine documents must be image pages.</summary>
    public const double MinImageDocumentRatio = 0.90;

    /// <summary>...and the book must average fewer visible characters per document than this.</summary>
    public const double MaxCharsPerDocument = 80;
}

/// <summary>What an EPUB's spine documents actually contain, computed during extraction.</summary>
public sealed record BookContentProfile(
    int TotalDocuments,
    int ImageDocuments,
    long TotalTextChars,
    bool DeclaresFixedLayout)
{
    public double ImageDocumentRatio => TotalDocuments == 0 ? 0 : (double)ImageDocuments / TotalDocuments;

    public double CharsPerDocument => TotalDocuments == 0 ? 0 : (double)TotalTextChars / TotalDocuments;

    /// <summary>True for comics / scanned books: almost every page is a picture and there is no text to work with.</summary>
    public bool IsImageOnly =>
        TotalDocuments > 0 &&
        ImageDocumentRatio >= ImageOnlyPolicy.MinImageDocumentRatio &&
        CharsPerDocument < ImageOnlyPolicy.MaxCharsPerDocument;

    /// <summary>Human-readable summary used in warnings and exception messages.</summary>
    public string Describe() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{ImageDocuments} of {TotalDocuments} pages are images ({ImageDocumentRatio:P0}), ~{CharsPerDocument:0} characters of text per page")
        + (DeclaresFixedLayout ? ", declares fixed layout" : "");
}
