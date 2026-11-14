using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Profiles the spine documents of an EPUB: how many are image pages and how much visible text there is
/// (spec 13). Regex based on purpose: fast, no DOM, and identical to the analysis that chose the thresholds.
/// Failure tolerant: a document that cannot be read is treated as text-bearing, never as an error (FR-7).
/// </summary>
public static partial class BookContentAnalyzer
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    // Ruby annotations (readings) are not "text" for this purpose.
    [GeneratedRegex(@"<(rt|rp)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RubyAnnotation();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<(img|image|svg)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ImageElement();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"rendition:layout""?[^<]*>\s*pre-paginated|name=""fixed-layout""\s+content=""true""|original-resolution|<meta[^>]+name=""book-type""[^>]+content=""comic""", RegexOptions.IgnoreCase)]
    private static partial Regex FixedLayoutDeclaration();

    public static BookContentProfile Analyze(ZipArchive archive, IReadOnlyList<string> spineHrefs, string opfXml)
    {
        int documents = 0;
        int imageDocuments = 0;
        long totalChars = 0;

        foreach (var href in spineHrefs)
        {
            string markup;
            try
            {
                var entry = EpubPaths.GetEntryNormalized(archive, href);
                if (entry is null) continue;
                markup = EpubArchiveReader.ReadEntryString(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
            {
                // Unreadable document: counts as a document with no signal that it is an image page.
                documents++;
                totalChars += ImageOnlyPolicy.ImagePageMaxChars + 1;
                continue;
            }

            documents++;
            int chars = CountVisibleChars(markup);
            totalChars += chars;
            if (chars <= ImageOnlyPolicy.ImagePageMaxChars && ImageElement().IsMatch(markup))
            {
                imageDocuments++;
            }
        }

        return new BookContentProfile(documents, imageDocuments, totalChars, FixedLayoutDeclaration().IsMatch(opfXml ?? ""));
    }

    public static int CountVisibleChars(string markup)
    {
        string text = ScriptOrStyle().Replace(markup, "");
        text = RubyAnnotation().Replace(text, "");
        text = AnyTag().Replace(text, "");
        return Whitespace().Replace(text, "").Length;
    }
}
