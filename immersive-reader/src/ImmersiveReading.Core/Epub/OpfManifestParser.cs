using AngleSharp.Dom;

namespace ImmersiveReading.Core.Epub;

internal static class OpfManifestParser
{
    public static Dictionary<string, (string Href, string MediaType, string Properties)> ParseManifest(IDocument opfDoc, string opfDir)
    {
        var map = new Dictionary<string, (string Href, string MediaType, string Properties)>(StringComparer.Ordinal);
        foreach (var item in opfDoc.All)
        {
            if (!string.Equals(item.LocalName, "item", StringComparison.OrdinalIgnoreCase)) continue;
            string? id = item.GetAttribute("id");
            string? href = item.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href)) continue;

            string resolvedHref = EpubPaths.ResolvePosixPath(opfDir, Uri.UnescapeDataString(href));
            string mediaType = item.GetAttribute("media-type") ?? "";
            string props = item.GetAttribute("properties") ?? "";
            map[id] = (resolvedHref, mediaType, props);
        }
        return map;
    }

    public static (List<string> SpineHrefs, string WritingMode) ParseSpine(
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest)
    {
        string writingMode = "auto";
        var spineHrefs = new List<string>();

        foreach (var el in opfDoc.All)
        {
            if (string.Equals(el.LocalName, "spine", StringComparison.OrdinalIgnoreCase))
            {
                string? ppd = el.GetAttribute("page-progression-direction");
                if (string.Equals(ppd, "rtl", StringComparison.OrdinalIgnoreCase))
                {
                    writingMode = "vertical-rl";
                }
            }
            else if (string.Equals(el.LocalName, "itemref", StringComparison.OrdinalIgnoreCase))
            {
                string? linear = el.GetAttribute("linear");
                if (string.Equals(linear, "no", StringComparison.OrdinalIgnoreCase)) continue;

                string? idref = el.GetAttribute("idref");
                if (!string.IsNullOrWhiteSpace(idref) && manifest.TryGetValue(idref, out var item))
                {
                    spineHrefs.Add(item.Href);
                }
            }
        }

        return (spineHrefs, writingMode);
    }
}

