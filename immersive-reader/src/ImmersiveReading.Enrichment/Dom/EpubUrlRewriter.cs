using AngleSharp.Dom;

namespace ImmersiveReading.Enrichment.Dom;

public static class EpubUrlRewriter
{
    public static string ResolveEpubRelativePath(string baseChapterHref, string relativeUrl, out string fragment)
    {
        fragment = "";
        string cleanUrl = relativeUrl.Trim();
        int hashIdx = cleanUrl.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragment = cleanUrl[hashIdx..];
            cleanUrl = cleanUrl[..hashIdx];
        }

        int queryIdx = cleanUrl.IndexOf('?');
        if (queryIdx >= 0)
        {
            cleanUrl = cleanUrl[..queryIdx];
        }

        cleanUrl = Uri.UnescapeDataString(cleanUrl).Replace('\\', '/');
        string normBase = (baseChapterHref ?? "").Replace('\\', '/').TrimStart('/');
        int lastSlash = normBase.LastIndexOf('/');
        string baseDir = lastSlash >= 0 ? normBase[..lastSlash] : "";

        string combined = cleanUrl.StartsWith('/')
            ? cleanUrl.TrimStart('/')
            : (string.IsNullOrEmpty(baseDir) ? cleanUrl : $"{baseDir}/{cleanUrl}");

        var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    public static bool IsExternalOrSpecialUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        string trimmed = url.Trim();
        return trimmed.StartsWith('#')
            || trimmed.StartsWith("ir-book://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ir-app://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    public static void RewriteRelativeAssetUrls(IDocument document, string bookId, string chapterHref)
    {
        if (string.IsNullOrWhiteSpace(bookId)) return;

        foreach (var el in document.All)
        {
            string tag = el.LocalName.ToLowerInvariant();

            if (tag is "img" or "script" or "audio" or "video" or "source")
            {
                string? src = el.GetAttribute("src");
                if (!IsExternalOrSpecialUrl(src))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, src!, out _);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("src", $"ir-book://{bookId}/{resolved}");
                    }
                }

                if (tag == "video")
                {
                    string? poster = el.GetAttribute("poster");
                    if (!IsExternalOrSpecialUrl(poster))
                    {
                        string resolved = ResolveEpubRelativePath(chapterHref, poster!, out _);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            el.SetAttribute("poster", $"ir-book://{bookId}/{resolved}");
                        }
                    }
                }
            }
            else if (tag == "link")
            {
                string? href = el.GetAttribute("href");
                if (!IsExternalOrSpecialUrl(href))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, href!, out _);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("href", $"ir-book://{bookId}/{resolved}");
                    }
                }
            }
            else if (tag == "image")
            {
                // SVG <image xlink:href="..."> or <image href="...">
                var hrefAttrs = el.Attributes
                    .Where(a => a.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                                a.Name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var attr in hrefAttrs)
                {
                    if (!IsExternalOrSpecialUrl(attr.Value))
                    {
                        string resolved = ResolveEpubRelativePath(chapterHref, attr.Value, out _);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            attr.Value = $"ir-book://{bookId}/{resolved}";
                        }
                    }
                }
            }
            else if (tag == "a")
            {
                string? href = el.GetAttribute("href");
                if (!IsExternalOrSpecialUrl(href))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, href!, out string fragment);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("data-ir-spine-href", resolved);
                        if (!string.IsNullOrEmpty(fragment))
                        {
                            el.SetAttribute("data-ir-fragment", fragment.TrimStart('#'));
                        }
                        el.SetAttribute("href", $"ir-book://{bookId}/{resolved}{fragment}");
                    }
                }
            }
        }
    }
}

