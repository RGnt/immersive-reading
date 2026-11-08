using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment;

public sealed class BookSchemeHandler
{
    private static readonly Regex CssUrlRegex = new(
        @"url\(\s*(?:""(?<url>[^""]+)""|'(?<url>[^']+)'|(?<url>[^)'""\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IBookCatalog _library;
    private readonly ILanguagePluginResolver _pluginRegistry;
    private readonly ChapterEnrichmentCache _cache;
    private readonly Func<string, string, WordStatus>? _getWordStatus;
    private EnrichmentOptions _enrichmentOptions;

    public Func<IReadOnlyList<IDomEnrichmentStage>>? DomStagesProvider { get; set; }

    public BookSchemeHandler(
        IBookCatalog library,
        ILanguagePluginResolver pluginRegistry,
        ChapterEnrichmentCache cache,
        EnrichmentOptions? initialOptions = null,
        Func<string, string, WordStatus>? getWordStatus = null,
        Func<IReadOnlyList<IDomEnrichmentStage>>? domStagesProvider = null)
    {
        _library = library;
        _pluginRegistry = pluginRegistry;
        _cache = cache;
        _enrichmentOptions = initialOptions ?? new EnrichmentOptions();
        _getWordStatus = getWordStatus;
        DomStagesProvider = domStagesProvider;
    }

    public void UpdateEnrichmentOptions(EnrichmentOptions options)
    {
        _enrichmentOptions = options;
    }

    public EnrichmentOptions CurrentOptions => _enrichmentOptions;

    public EnrichedChapterResult? GetEnrichedChapter(string bookId, string chapterHref)
    {
        if (string.IsNullOrWhiteSpace(bookId) || string.IsNullOrWhiteSpace(chapterHref))
        {
            return null;
        }

        string relativePath = Uri.UnescapeDataString(chapterHref.TrimStart('/'));
        int hashIdx = relativePath.IndexOf('#');
        if (hashIdx >= 0)
        {
            relativePath = relativePath[..hashIdx];
        }

        if (relativePath.Contains("..", StringComparison.Ordinal) || relativePath.StartsWith('/'))
        {
            return null;
        }

        var bookDetail = _library.GetBookDetail(bookId);
        if (bookDetail is null)
        {
            return null;
        }

        string fullEpubPath = Path.Combine(bookDetail.Summary.FolderPath, bookDetail.Summary.EpubFileName);
        if (!File.Exists(fullEpubPath))
        {
            return null;
        }

        using var archive = new ZipArchive(
            new FileStream(fullEpubPath, FileMode.Open, FileAccess.Read, FileShare.Read),
            ZipArchiveMode.Read);

        var entry = EpubMetadataExtractor.GetEntryNormalized(archive, relativePath);
        if (entry is null)
        {
            return null;
        }

        string rawXhtml;
        using (var stream = entry.Open())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            rawXhtml = reader.ReadToEnd();
        }

        var plugin = _pluginRegistry.Resolve(bookDetail.Summary.Language);
        var domStages = DomStagesProvider?.Invoke();
        var enriched = AngleSharpDomEnricher.EnrichChapter(
            rawXhtml,
            bookId,
            relativePath,
            plugin,
            _enrichmentOptions,
            getWordStatus: _getWordStatus,
            domStages: domStages);

        _cache.Set(enriched.TokenMap);
        return enriched;
    }

    public Stream Handle(string url, out string contentType)
    {
        contentType = "text/plain; charset=utf-8";
        try
        {
            var uri = new Uri(url);
            string bookId = uri.Host; // ir-book://book-4e0541b184c4ace9/item/xhtml/p-001.xhtml
            string relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

            // Security: Path traversal protection (Scenario 3 in 05-photino-reader-and-study-pack-bridge.feature)
            if (relativePath.Contains("..", StringComparison.Ordinal) || relativePath.StartsWith('/'))
            {
                return new MemoryStream([]);
            }

            string ext = Path.GetExtension(relativePath).ToLowerInvariant();
            if (ext is ".xhtml" or ".html" or ".htm")
            {
                var enriched = GetEnrichedChapter(bookId, relativePath);
                if (enriched is null)
                {
                    return new MemoryStream([]);
                }

                contentType = "application/xhtml+xml; charset=utf-8";
                return new MemoryStream(Encoding.UTF8.GetBytes(enriched.EnrichedHtml));
            }

            var bookDetail = _library.GetBookDetail(bookId);
            if (bookDetail is null)
            {
                return new MemoryStream([]);
            }

            string fullEpubPath = Path.Combine(bookDetail.Summary.FolderPath, bookDetail.Summary.EpubFileName);
            if (!File.Exists(fullEpubPath))
            {
                return new MemoryStream([]);
            }

            using var archive = new ZipArchive(
                new FileStream(fullEpubPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                ZipArchiveMode.Read);

            var entry = EpubMetadataExtractor.GetEntryNormalized(archive, relativePath);
            if (entry is null)
            {
                return new MemoryStream([]);
            }

            if (ext == ".css")
            {
                contentType = "text/css; charset=utf-8";
                string rawCss;
                using (var stream = entry.Open())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    rawCss = reader.ReadToEnd();
                }

                string rewrittenCss = RewriteCssRelativeUrls(rawCss, bookId, relativePath);
                return new MemoryStream(Encoding.UTF8.GetBytes(rewrittenCss));
            }

            // Stream relative assets (.jpg, .png, .woff2, .svg, etc.)
            contentType = GetMimeType(relativePath);
            var ms = new MemoryStream((int)entry.Length);
            using (var stream = entry.Open())
            {
                stream.CopyTo(ms);
            }
            ms.Position = 0;
            return ms;
        }
        catch
        {
            return new MemoryStream([]);
        }
    }

    public static string RewriteCssRelativeUrls(string rawCss, string bookId, string cssEntryPath)
    {
        if (string.IsNullOrEmpty(rawCss)) return string.Empty;

        return CssUrlRegex.Replace(rawCss, match =>
        {
            string rawUrl = match.Groups["url"].Value.Trim();
            if (string.IsNullOrEmpty(rawUrl)
                || rawUrl.StartsWith('#')
                || rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || rawUrl.StartsWith("ir-book://", StringComparison.OrdinalIgnoreCase)
                || rawUrl.StartsWith("ir-app://", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            string resolved = AngleSharpDomEnricher.ResolveEpubRelativePath(cssEntryPath, rawUrl, out string fragment);
            if (string.IsNullOrEmpty(resolved))
            {
                return match.Value;
            }

            return $"url(\"ir-book://{bookId}/{resolved}{fragment}\")";
        });
    }

    private static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            _ => "application/octet-stream"
        };
    }
}
