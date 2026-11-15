using System.IO.Compression;
using System.Text;
using AngleSharp.Dom;

namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Reads the visible running text of an EPUB, one string per paragraph, in reading order.
/// Ruby readings (rt/rp), scripts and styles are dropped so only the base text is counted.
/// </summary>
public static class BookTextExtractor
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "tr", "table", "blockquote",
        "section", "article", "aside", "dt", "dd", "figcaption", "pre", "header", "footer"
    };

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "rt", "rp", "head", "title"
    };

    public static IReadOnlyList<string> ExtractParagraphs(string epubPath, IEnumerable<string> spineHrefs)
    {
        using var archive = ZipFile.OpenRead(epubPath);
        var paragraphs = new List<string>();
        foreach (string href in spineHrefs)
        {
            var entry = EpubPaths.GetEntryNormalized(archive, href);
            if (entry is null) continue;
            paragraphs.AddRange(ExtractFromMarkup(EpubArchiveReader.ReadEntryString(entry)));
        }
        return paragraphs;
    }

    public static IReadOnlyList<string> ExtractFromMarkup(string markup)
    {
        var doc = EpubArchiveReader.ParseXmlOrHtml(markup);
        var result = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length == 0) return;
            string text = Normalize(current.ToString());
            current.Clear();
            if (text.Length > 0) result.Add(text);
        }

        void Walk(INode node)
        {
            switch (node)
            {
                case IText text:
                    current.Append(text.Data);
                    break;
                case IElement el:
                    if (Skipped.Contains(el.LocalName)) return;
                    bool block = Blocks.Contains(el.LocalName);
                    bool lineBreak = el.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase);
                    if (block || lineBreak) Flush();
                    foreach (var child in el.ChildNodes) Walk(child);
                    if (block) Flush();
                    break;
            }
        }

        if (doc.Body is not null) Walk(doc.Body);
        Flush();
        return result;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool lastSpace = false;
        foreach (char c in text)
        {
            if (c == '　') continue; // ideographic space is layout indentation, never a word break
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(c);
                lastSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
