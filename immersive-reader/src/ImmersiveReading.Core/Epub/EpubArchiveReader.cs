using System.IO.Compression;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ImmersiveReading.Core.Epub;

internal static class EpubArchiveReader
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
        return htmlParser.ParseDocument(markup);
    }
}

