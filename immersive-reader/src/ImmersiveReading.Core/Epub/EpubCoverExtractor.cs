using System.IO.Compression;
using AngleSharp.Dom;

namespace ImmersiveReading.Core.Epub;

internal static class EpubCoverExtractor
{
    public static (byte[]? Bytes, string? Extension) ExtractCoverImage(
        ZipArchive archive,
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest)
    {
        string? coverHref = null;

        // 1. EPUB3 properties="cover-image"
        foreach (var kv in manifest.Values)
        {
            if (kv.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Contains("cover-image", StringComparer.OrdinalIgnoreCase))
            {
                coverHref = kv.Href;
                break;
            }
        }

        // 2. EPUB2 <meta name="cover" content="item-id"/>
        if (coverHref is null)
        {
            foreach (var el in opfDoc.All)
            {
                if (string.Equals(el.LocalName, "meta", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(el.GetAttribute("name"), "cover", StringComparison.OrdinalIgnoreCase))
                {
                    string? coverId = el.GetAttribute("content")?.Trim();
                    if (!string.IsNullOrEmpty(coverId) && manifest.TryGetValue(coverId, out var item))
                    {
                        coverHref = item.Href;
                        break;
                    }
                }
            }
        }

        // 3. Fallback: manifest item with id/href containing "cover" and image media-type
        if (coverHref is null)
        {
            foreach (var kv in manifest)
            {
                if (kv.Value.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                    (kv.Key.Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                     kv.Value.Href.Contains("cover", StringComparison.OrdinalIgnoreCase)))
                {
                    coverHref = kv.Value.Href;
                    break;
                }
            }
        }

        if (coverHref is null) return (null, null);

        var entry = EpubPaths.GetEntryNormalized(archive, coverHref);
        if (entry is null || entry.Length > 15 * 1024 * 1024) return (null, null);

        using var ms = new MemoryStream((int)entry.Length);
        using (var s = entry.Open())
        {
            s.CopyTo(ms);
        }

        string ext = Path.GetExtension(coverHref).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
        return (ms.ToArray(), ext);
    }
}

