using System.IO.Compression;
using AngleSharp.Dom;

namespace ImmersiveReading.Core.Epub;

public sealed record EpubNavPoint(
    string Title,
    string SpineHref,
    string FragmentId,
    int Order
);

internal static class EpubTocParser
{
    public static List<EpubNavPoint> ParseTableOfContents(
        ZipArchive archive,
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest)
    {
        var list = new List<EpubNavPoint>();

        // 1. Try EPUB 3 Nav Document (item with property="nav")
        foreach (var item in manifest.Values)
        {
            if (item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("nav", StringComparer.OrdinalIgnoreCase))
            {
                var navEntry = EpubPaths.GetEntryNormalized(archive, item.Href);
                if (navEntry is not null)
                {
                    string navDir = EpubPaths.GetPosixDirectory(item.Href);
                    var navDoc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(navEntry));

                    // Prefer <nav epub:type="toc">, <nav role="doc-toc">, or <nav id="toc">
                    IElement? tocNav = null;
                    foreach (var navEl in navDoc.QuerySelectorAll("nav"))
                    {
                        string epubType = navEl.GetAttribute("epub:type") ?? "";
                        string role = navEl.GetAttribute("role") ?? "";
                        string id = navEl.GetAttribute("id") ?? "";
                        string cls = navEl.GetAttribute("class") ?? "";

                        if (epubType.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("toc", StringComparer.OrdinalIgnoreCase) ||
                            string.Equals(role, "doc-toc", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(id, "toc", StringComparison.OrdinalIgnoreCase) ||
                            cls.Contains("toc", StringComparison.OrdinalIgnoreCase))
                        {
                            tocNav = navEl;
                            break;
                        }
                    }

                    if (tocNav is null)
                    {
                        foreach (var navEl in navDoc.QuerySelectorAll("nav"))
                        {
                            string epubType = navEl.GetAttribute("epub:type") ?? "";
                            if (!epubType.Contains("landmarks", StringComparison.OrdinalIgnoreCase) &&
                                !epubType.Contains("page-list", StringComparison.OrdinalIgnoreCase))
                            {
                                tocNav = navEl;
                                break;
                            }
                        }
                    }

                    if (tocNav is not null)
                    {
                        int order = 1;
                        foreach (var a in tocNav.QuerySelectorAll("a[href]"))
                        {
                            string? rawHref = a.GetAttribute("href");
                            string label = a.TextContent.Trim();
                            if (string.IsNullOrWhiteSpace(rawHref) || string.IsNullOrWhiteSpace(label)) continue;

                            int hashIdx = rawHref.IndexOf('#');
                            string pathPart = hashIdx >= 0 ? rawHref[..hashIdx] : rawHref;
                            string fragPart = hashIdx >= 0 ? rawHref[(hashIdx + 1)..] : "";
                            if (string.IsNullOrWhiteSpace(pathPart)) continue;

                            string resolved = EpubPaths.ResolvePosixPath(navDir, Uri.UnescapeDataString(pathPart));
                            list.Add(new EpubNavPoint(label, resolved, fragPart, order++));
                        }
                    }
                }
            }
        }

        if (list.Count > 0) return list;

        // 2. Fallback to EPUB 2 NCX
        foreach (var item in manifest.Values)
        {
            if (string.Equals(item.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase) ||
                item.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase))
            {
                var ncxEntry = EpubPaths.GetEntryNormalized(archive, item.Href);
                if (ncxEntry is not null)
                {
                    string ncxDir = EpubPaths.GetPosixDirectory(item.Href);
                    var ncxDoc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(ncxEntry));
                    int order = 1;

                    foreach (var np in ncxDoc.All)
                    {
                        if (!string.Equals(np.LocalName, "navPoint", StringComparison.OrdinalIgnoreCase)) continue;

                        string? label = null;
                        string? src = null;

                        foreach (var child in np.Children)
                        {
                            if (string.Equals(child.LocalName, "navLabel", StringComparison.OrdinalIgnoreCase))
                            {
                                label = child.TextContent.Trim();
                            }
                            else if (string.Equals(child.LocalName, "content", StringComparison.OrdinalIgnoreCase))
                            {
                                src = child.GetAttribute("src")?.Trim();
                            }
                        }

                        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(src))
                        {
                            var txtEl = np.QuerySelector("navLabel > text, text");
                            label ??= txtEl?.TextContent.Trim();
                            var cntEl = np.QuerySelector("content");
                            src ??= cntEl?.GetAttribute("src")?.Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(src))
                        {
                            int hashIdx = src.IndexOf('#');
                            string pathPart = hashIdx >= 0 ? src[..hashIdx] : src;
                            string fragPart = hashIdx >= 0 ? src[(hashIdx + 1)..] : "";
                            string resolved = EpubPaths.ResolvePosixPath(ncxDir, Uri.UnescapeDataString(pathPart));
                            list.Add(new EpubNavPoint(label, resolved, fragPart, order++));
                        }
                    }
                }
            }
        }

        return list;
    }
}

