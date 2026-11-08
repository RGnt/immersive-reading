using System.IO.Compression;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ImmersiveReading.Core.Epub;

public static class EpubArchiveReader
{
    public static string? FindOpfPath(ZipArchive archive)
    {
        var containerEntry = EpubPaths.GetEntryNormalized(archive, "META-INF/container.xml");
        if (containerEntry is null) return null;

        var doc = ParseXmlOrHtml(ReadEntryString(containerEntry));
        foreach (var el in doc.All)
        {
            if (string.Equals(el.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))
            {
                string? fullPath = el.GetAttribute("full-path")?.Trim();
                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    return EpubPaths.NormalizePosixPath(fullPath);
                }
            }
        }
        return null;
    }

    public static string? FindFirstOpfEntry(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            {
                return EpubPaths.NormalizePosixPath(entry.FullName);
            }
        }
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex SelfClosingNonVoidRegex = new(
        @"<(?<tag>script|style|div|span|p|iframe|title|textarea|a|b|i|strong|em|ruby|rt|rp|button)(?<attrs>(?:\s+[^""'>]+|""[^""]*""|'[^']*')*?)\s*/>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Expands self-closing non-void XML/XHTML tags (e.g. &lt;script src="..." /&gt; or &lt;style ... /&gt;)
    /// into explicit start and end tags so HTML5 parsers (AngleSharp and browser DOM) do not treat
    /// them as unclosed rawtext tags that swallow following content.
    /// </summary>
    public static string NormalizeXhtmlSelfClosingTags(string markup)
    {
        if (string.IsNullOrEmpty(markup)) return string.Empty;
        return SelfClosingNonVoidRegex.Replace(markup, "<${tag}${attrs}></${tag}>");
    }

    public static string ReadEntryString(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static IDocument ParseXmlOrHtml(string markup)
    {
        var htmlParser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = false
        });
        string normalized = NormalizeXhtmlSelfClosingTags(markup);
        return htmlParser.ParseDocument(normalized);
    }
}

