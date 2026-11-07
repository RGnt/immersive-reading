using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

public sealed record ExtractedEpubPackage(
    string BookId,
    string EpubSha256,
    long FileSizeBytes,
    string Title,
    string SortTitle,
    string Authors,
    string AuthorSort,
    string Isbn,
    string Publisher,
    string Imprint,
    string PublishedDate,
    string Language,
    string Description,
    string SeriesName,
    double? SeriesIndex,
    string WritingMode,
    OpfOriginalMetadata OpfOriginal,
    byte[]? CoverImageBytes,
    string? CoverExtension,
    IReadOnlyList<BookChapterRecord> Chapters,
    int TotalCharCount,
    bool StatsComputed
);

public static class EpubMetadataExtractor
{
    private static readonly HashSet<string> FrontBackMatterTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "もくじ", "目次", "奥付", "表紙", "扉", "裏表紙", "著作権", "クレジット",
        "contents", "table of contents", "toc", "cover", "copyright", "colophon", "title page", "acknowledgments"
    };

    public static async Task<ExtractedEpubPackage> ExtractFromFileAsync(
        string epubFilePath,
        bool computeDeepChapterStats = false,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(epubFilePath);
        var (bookId, sha256) = await BookIdentity.ComputeFromFileAsync(epubFilePath, ct).ConfigureAwait(false);

        await using var fs = new FileStream(
            epubFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32768,
            useAsync: true);

        return ExtractFromStream(
            fs,
            Path.GetFileName(epubFilePath),
            bookId,
            sha256,
            fileInfo.Length,
            computeDeepChapterStats);
    }

    public static ExtractedEpubPackage ExtractFromStream(
        Stream epubStream,
        string fileName,
        string? precomputedBookId = null,
        string? precomputedSha256 = null,
        long fileSizeBytes = 0,
        bool computeDeepChapterStats = false)
    {
        string bookId;
        string sha256;
        if (precomputedBookId is not null && precomputedSha256 is not null)
        {
            bookId = precomputedBookId;
            sha256 = precomputedSha256;
        }
        else
        {
            (bookId, sha256) = BookIdentity.ComputeFromStream(epubStream);
        }

        if (fileSizeBytes <= 0 && epubStream.CanSeek)
        {
            fileSizeBytes = epubStream.Length;
        }

        using var archive = new ZipArchive(epubStream, ZipArchiveMode.Read, leaveOpen: true);

        // 1. Locate OPF path from META-INF/container.xml
        string opfPath = FindOpfPath(archive) ?? FindFirstOpfEntry(archive)
            ?? throw new InvalidDataException($"No OPF package found in EPUB '{fileName}'.");

        var opfEntry = GetEntryNormalized(archive, opfPath)
            ?? throw new InvalidDataException($"OPF entry '{opfPath}' not found in EPUB '{fileName}'.");

        string opfDir = GetPosixDirectory(opfPath);
        string opfXml = ReadEntryString(opfEntry);
        var opfDoc = ParseXmlOrHtml(opfXml);

        // 2. Extract raw OPF metadata (using XDocument for full XML <meta> text content fidelity)
        var rawOpf = ParseOpfMetadataXml(opfXml, opfDoc);

        // 3. Apply filename & title heuristics when OPF fields are missing or bracket-encoded
        var fileHints = FilenameMetadataParser.Parse(fileName);
        var titleHints = FilenameMetadataParser.Parse(rawOpf.Title);

        string rawTitleClean = !string.IsNullOrWhiteSpace(rawOpf.Title)
            ? rawOpf.Title.Trim()
            : fileHints.CleanTitle;

        string effectiveTitle = rawTitleClean;
        string effectiveAuthors = rawOpf.Authors;
        string effectiveImprint = rawOpf.Imprint;
        string effectiveSeriesName = rawOpf.SeriesName;
        double? effectiveSeriesIndex = rawOpf.SeriesIndex;
        string effectiveIsbn = rawOpf.Isbn;

        // If OPF lacks author or title starts with [Author], enrich from hints
        if (string.IsNullOrWhiteSpace(effectiveAuthors))
        {
            effectiveAuthors = !string.IsNullOrWhiteSpace(titleHints.Authors)
                ? titleHints.Authors
                : fileHints.Authors;
            if (!string.IsNullOrWhiteSpace(titleHints.Authors) && !string.IsNullOrWhiteSpace(titleHints.CleanTitle))
            {
                effectiveTitle = titleHints.CleanTitle;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveImprint))
        {
            effectiveImprint = !string.IsNullOrWhiteSpace(titleHints.Imprint)
                ? titleHints.Imprint
                : fileHints.Imprint;
        }

        if (string.IsNullOrWhiteSpace(effectiveSeriesName))
        {
            effectiveSeriesName = !string.IsNullOrWhiteSpace(titleHints.SeriesName)
                ? titleHints.SeriesName
                : fileHints.SeriesName;
        }

        effectiveSeriesIndex ??= titleHints.SeriesIndex ?? fileHints.SeriesIndex;

        if (string.IsNullOrWhiteSpace(effectiveIsbn))
        {
            effectiveIsbn = !string.IsNullOrWhiteSpace(titleHints.Isbn)
                ? titleHints.Isbn
                : fileHints.Isbn;
        }

        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            effectiveTitle = Path.GetFileNameWithoutExtension(fileName);
        }

        string effectiveSortTitle = !string.IsNullOrWhiteSpace(rawOpf.SortTitle)
            ? rawOpf.SortTitle
            : effectiveTitle;

        string effectiveAuthorSort = !string.IsNullOrWhiteSpace(rawOpf.AuthorSort)
            ? rawOpf.AuthorSort
            : effectiveAuthors;

        // 4. Parse manifest & spine
        var manifest = ParseManifest(opfDoc, opfDir);
        var (spineHrefs, writingMode) = ParseSpine(opfDoc, manifest);
        if (rawOpf.WritingMode != "auto")
        {
            writingMode = rawOpf.WritingMode;
        }

        // 5. Extract Cover Image
        var (coverBytes, coverExt) = ExtractCoverImage(archive, opfDoc, manifest);

        // 6. Build Chapter list (and optional deep character count)
        var tocMap = ParseNavigationTitles(archive, opfDoc, manifest);
        var chapters = new List<BookChapterRecord>(spineHrefs.Count);
        int cumulativeChars = 0;

        for (int i = 0; i < spineHrefs.Count; i++)
        {
            string spineHref = spineHrefs[i];
            string chapterTitle = tocMap.TryGetValue(spineHref, out string? navTitle) && !string.IsNullOrWhiteSpace(navTitle)
                ? navTitle
                : $"Chapter {i + 1}";

            bool isExcluded = IsFrontOrBackMatter(chapterTitle, spineHref);
            int charCount = 0;

            if (computeDeepChapterStats)
            {
                var chEntry = GetEntryNormalized(archive, spineHref);
                if (chEntry is not null)
                {
                    charCount = CountNormalizedChapterCharacters(chEntry);
                }
            }

            chapters.Add(new BookChapterRecord(
                BookId: bookId,
                ChapterIndex: i,
                ChapterId: $"chapter-{i + 1:D2}",
                Title: chapterTitle,
                SpineHref: spineHref,
                FragmentId: "",
                EndSpineHref: null,
                EndFragmentId: null,
                CharCount: charCount,
                CumulativeChars: cumulativeChars,
                IsExcluded: isExcluded));

            if (!isExcluded)
            {
                cumulativeChars += charCount;
            }
        }

        var opfSnapshot = rawOpf with
        {
            Title = !string.IsNullOrWhiteSpace(rawOpf.Title) ? rawOpf.Title : effectiveTitle,
            WritingMode = writingMode
        };

        return new ExtractedEpubPackage(
            BookId: bookId,
            EpubSha256: sha256,
            FileSizeBytes: fileSizeBytes,
            Title: effectiveTitle,
            SortTitle: effectiveSortTitle,
            Authors: effectiveAuthors,
            AuthorSort: effectiveAuthorSort,
            Isbn: effectiveIsbn,
            Publisher: rawOpf.Publisher,
            Imprint: effectiveImprint,
            PublishedDate: rawOpf.PublishedDate,
            Language: rawOpf.Language,
            Description: rawOpf.Description,
            SeriesName: effectiveSeriesName,
            SeriesIndex: effectiveSeriesIndex,
            WritingMode: writingMode,
            OpfOriginal: opfSnapshot,
            CoverImageBytes: coverBytes,
            CoverExtension: coverExt,
            Chapters: chapters,
            TotalCharCount: cumulativeChars,
            StatsComputed: computeDeepChapterStats);
    }

    private static OpfOriginalMetadata ParseOpfMetadataXml(string opfXml, IDocument fallbackOpfDoc)
    {
        try
        {
            var xdoc = XDocument.Parse(opfXml, LoadOptions.None);
            var refinesMap = new Dictionary<string, List<(string Property, string Value)>>(StringComparer.OrdinalIgnoreCase);
            var metaEls = xdoc.Descendants().Where(e => string.Equals(e.Name.LocalName, "meta", StringComparison.OrdinalIgnoreCase)).ToList();

            string? calibreSeries = null;
            double? calibreSeriesIndex = null;
            string? collectionSeries = null;
            double? collectionPosition = null;
            string writingMode = "auto";

            foreach (var meta in metaEls)
            {
                string? prop = meta.Attribute("property")?.Value.Trim();
                string? refines = meta.Attribute("refines")?.Value.Trim();
                string? name = meta.Attribute("name")?.Value.Trim();
                string? content = meta.Attribute("content")?.Value.Trim();
                string val = !string.IsNullOrWhiteSpace(meta.Value) ? meta.Value.Trim() : (content ?? "");

                if (!string.IsNullOrEmpty(prop) && !string.IsNullOrEmpty(refines))
                {
                    if (!refinesMap.TryGetValue(refines, out var list))
                    {
                        list = [];
                        refinesMap[refines] = list;
                    }
                    list.Add((prop, val));
                }

                if (string.Equals(prop, "belongs-to-collection", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                {
                    collectionSeries = val;
                }
                else if (string.Equals(prop, "group-position", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrEmpty(refines) &&
                         double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double topGp))
                {
                    collectionPosition = topGp;
                }

                if (string.Equals(name, "calibre:series", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(content))
                {
                    calibreSeries = content;
                }
                else if (string.Equals(name, "calibre:series_index", StringComparison.OrdinalIgnoreCase) &&
                         double.TryParse(content, NumberStyles.Float, CultureInfo.InvariantCulture, out double cIdx))
                {
                    calibreSeriesIndex = cIdx;
                }
                else if (string.Equals(name, "primary-writing-mode", StringComparison.OrdinalIgnoreCase) &&
                         content?.Contains("vertical", StringComparison.OrdinalIgnoreCase) == true)
                {
                    writingMode = "vertical-rl";
                }
            }

            foreach (var meta in metaEls)
            {
                string? prop = meta.Attribute("property")?.Value.Trim();
                string? id = meta.Attribute("id")?.Value.Trim();
                if (string.Equals(prop, "belongs-to-collection", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(id) &&
                    refinesMap.TryGetValue("#" + id, out var subProps))
                {
                    foreach (var sp in subProps)
                    {
                        if (string.Equals(sp.Property, "group-position", StringComparison.OrdinalIgnoreCase) &&
                            double.TryParse(sp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gp))
                        {
                            collectionPosition = gp;
                        }
                    }
                }
            }

            static string? GetXFileAs(XElement el, Dictionary<string, List<(string Property, string Value)>> refMap)
            {
                foreach (var attr in el.Attributes())
                {
                    if (attr.Name.LocalName.Equals("file-as", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(attr.Value))
                    {
                        return attr.Value.Trim();
                    }
                }

                string? id = el.Attribute("id")?.Value.Trim();
                if (!string.IsNullOrEmpty(id) && refMap.TryGetValue("#" + id, out var props))
                {
                    foreach (var p in props)
                    {
                        if (string.Equals(p.Property, "file-as", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
                        {
                            return p.Value;
                        }
                    }
                }
                return null;
            }

            var titleEl = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase));
            string title = titleEl?.Value.Trim() ?? "";
            string sortTitle = titleEl is not null ? (GetXFileAs(titleEl, refinesMap) ?? "") : "";

            var creatorEls = xdoc.Descendants().Where(e => string.Equals(e.Name.LocalName, "creator", StringComparison.OrdinalIgnoreCase)).ToList();
            var authorsList = new List<string>();
            var authorSortsList = new List<string>();
            foreach (var cr in creatorEls)
            {
                string cName = cr.Value.Trim();
                if (string.IsNullOrWhiteSpace(cName)) continue;
                authorsList.Add(cName);
                string? fa = GetXFileAs(cr, refinesMap);
                if (!string.IsNullOrWhiteSpace(fa))
                {
                    authorSortsList.Add(fa);
                }
            }

            string authors = string.Join(" · ", authorsList.Distinct(StringComparer.OrdinalIgnoreCase));
            string authorSort = authorSortsList.Count > 0
                ? string.Join(" · ", authorSortsList.Distinct(StringComparer.OrdinalIgnoreCase))
                : authors;

            string publisher = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "publisher", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
            string rawDate = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "date", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
            string publishedDate = rawDate.Length >= 10 && rawDate[4] == '-' && rawDate[7] == '-' ? rawDate[..10] : rawDate;

            string rawLang = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "language", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "ja";
            string language = NormalizeLanguageTag(rawLang);
            string description = xdoc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "description", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";

            string opfIdentifier = "";
            string isbn = "";
            foreach (var idEl in xdoc.Descendants().Where(e => string.Equals(e.Name.LocalName, "identifier", StringComparison.OrdinalIgnoreCase)))
            {
                string val = idEl.Value.Trim();
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (string.IsNullOrEmpty(opfIdentifier)) opfIdentifier = val;
                string candidateIsbn = FilenameMetadataParser.ExtractIsbn(val);
                if (!string.IsNullOrEmpty(candidateIsbn))
                {
                    isbn = candidateIsbn;
                    break;
                }
            }

            return new OpfOriginalMetadata(
                Title: title,
                SortTitle: sortTitle,
                Authors: authors,
                AuthorSort: authorSort,
                Isbn: isbn,
                Publisher: publisher,
                Imprint: "",
                PublishedDate: publishedDate,
                Language: language,
                Description: description,
                OpfIdentifier: opfIdentifier,
                SeriesName: collectionSeries ?? calibreSeries ?? "",
                SeriesIndex: collectionPosition ?? calibreSeriesIndex,
                WritingMode: writingMode);
        }
        catch
        {
            return ParseOpfMetadata(fallbackOpfDoc);
        }
    }

    private static OpfOriginalMetadata ParseOpfMetadata(IDocument opfDoc)
    {
        // Build EPUB3 refines map: "#id" -> list of (property, textContent)
        var refinesMap = new Dictionary<string, List<(string Property, string Value)>>(StringComparer.OrdinalIgnoreCase);
        var metaElements = opfDoc.QuerySelectorAll("metadata > meta, meta");
        string? calibreSeries = null;
        double? calibreSeriesIndex = null;
        string? collectionSeries = null;
        double? collectionPosition = null;
        string writingMode = "auto";

        foreach (var meta in metaElements)
        {
            string? prop = meta.GetAttribute("property")?.Trim();
            string? refines = meta.GetAttribute("refines")?.Trim();
            string? name = meta.GetAttribute("name")?.Trim();
            string? content = meta.GetAttribute("content")?.Trim();
            string val = !string.IsNullOrWhiteSpace( meta.TextContent ) ? meta.TextContent.Trim() : (content ?? "");

            if (!string.IsNullOrEmpty(prop) && !string.IsNullOrEmpty(refines))
            {
                if (!refinesMap.TryGetValue(refines, out var list))
                {
                    list = [];
                    refinesMap[refines] = list;
                }
                list.Add((prop, val));
            }

            if (string.Equals(prop, "belongs-to-collection", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
            {
                collectionSeries = val;
                string? id = meta.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(id) && refinesMap.TryGetValue("#" + id, out var subProps))
                {
                    foreach (var sp in subProps)
                    {
                        if (string.Equals(sp.Property, "group-position", StringComparison.OrdinalIgnoreCase) &&
                            double.TryParse(sp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gp))
                        {
                            collectionPosition = gp;
                        }
                    }
                }
            }
            else if (string.Equals(prop, "group-position", StringComparison.OrdinalIgnoreCase) &&
                     string.IsNullOrEmpty(refines) &&
                     double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out double topGp))
            {
                collectionPosition = topGp;
            }

            if (string.Equals(name, "calibre:series", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(content))
            {
                calibreSeries = content;
            }
            else if (string.Equals(name, "calibre:series_index", StringComparison.OrdinalIgnoreCase) &&
                     double.TryParse(content, NumberStyles.Float, CultureInfo.InvariantCulture, out double cIdx))
            {
                calibreSeriesIndex = cIdx;
            }
            else if (string.Equals(name, "primary-writing-mode", StringComparison.OrdinalIgnoreCase) &&
                     content?.Contains("vertical", StringComparison.OrdinalIgnoreCase) == true)
            {
                writingMode = "vertical-rl";
            }
        }

        // Check second pass for refines on belongs-to-collection if group-position appeared earlier
        foreach (var meta in metaElements)
        {
            string? prop = meta.GetAttribute("property")?.Trim();
            string? id = meta.GetAttribute("id")?.Trim();
            if (string.Equals(prop, "belongs-to-collection", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(id) &&
                refinesMap.TryGetValue("#" + id, out var subProps))
            {
                foreach (var sp in subProps)
                {
                    if (string.Equals(sp.Property, "group-position", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(sp.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gp))
                    {
                        collectionPosition = gp;
                    }
                }
            }
        }

        // Title & SortTitle
        string title = "";
        string sortTitle = "";
        var titleEl = FindDcElement(opfDoc, "title");
        if (titleEl is not null)
        {
            title = titleEl.TextContent.Trim();
            sortTitle = GetFileAs(titleEl, refinesMap) ?? "";
        }

        // Creators / Authors
        var creatorEls = FindDcElements(opfDoc, "creator");
        var authorsList = new List<string>();
        var authorSortsList = new List<string>();
        foreach (var cr in creatorEls)
        {
            string name = cr.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            authorsList.Add(name);
            string? fa = GetFileAs(cr, refinesMap);
            if (!string.IsNullOrWhiteSpace(fa))
            {
                authorSortsList.Add(fa);
            }
        }

        string authors = string.Join(" · ", authorsList.Distinct(StringComparer.OrdinalIgnoreCase));
        string authorSort = authorSortsList.Count > 0
            ? string.Join(" · ", authorSortsList.Distinct(StringComparer.OrdinalIgnoreCase))
            : authors;

        string publisher = FindDcElement(opfDoc, "publisher")?.TextContent.Trim() ?? "";
        string rawDate = FindDcElement(opfDoc, "date")?.TextContent.Trim() ?? "";
        string publishedDate = rawDate.Length >= 10 && rawDate[4] == '-' && rawDate[7] == '-'
            ? rawDate[..10]
            : rawDate;

        string rawLang = FindDcElement(opfDoc, "language")?.TextContent.Trim() ?? "ja";
        string language = NormalizeLanguageTag(rawLang);
        string description = FindDcElement(opfDoc, "description")?.TextContent.Trim() ?? "";

        // Identifiers & ISBN
        string opfIdentifier = "";
        string isbn = "";
        foreach (var idEl in FindDcElements(opfDoc, "identifier"))
        {
            string val = idEl.TextContent.Trim();
            if (string.IsNullOrWhiteSpace(val)) continue;
            if (string.IsNullOrEmpty(opfIdentifier))
            {
                opfIdentifier = val;
            }

            string candidateIsbn = FilenameMetadataParser.ExtractIsbn(val);
            if (!string.IsNullOrEmpty(candidateIsbn))
            {
                isbn = candidateIsbn;
                break;
            }
        }

        string seriesName = collectionSeries ?? calibreSeries ?? "";
        double? seriesIndex = collectionPosition ?? calibreSeriesIndex;

        return new OpfOriginalMetadata(
            Title: title,
            SortTitle: sortTitle,
            Authors: authors,
            AuthorSort: authorSort,
            Isbn: isbn,
            Publisher: publisher,
            Imprint: "",
            PublishedDate: publishedDate,
            Language: language,
            Description: description,
            OpfIdentifier: opfIdentifier,
            SeriesName: seriesName,
            SeriesIndex: seriesIndex,
            WritingMode: writingMode);
    }

    private static string? GetFileAs(IElement element, Dictionary<string, List<(string Property, string Value)>> refinesMap)
    {
        foreach (var attr in element.Attributes)
        {
            if (attr.Name.EndsWith("file-as", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
            {
                return attr.Value.Trim();
            }
        }

        string? id = element.GetAttribute("id")?.Trim();
        if (!string.IsNullOrEmpty(id) && refinesMap.TryGetValue("#" + id, out var props))
        {
            foreach (var p in props)
            {
                if (string.Equals(p.Property, "file-as", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value))
                {
                    return p.Value;
                }
            }
        }

        return null;
    }

    private static IElement? FindDcElement(IDocument doc, string localName)
    {
        foreach (var el in doc.All)
        {
            if (string.Equals(el.LocalName, localName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(el.TagName, $"dc:{localName}", StringComparison.OrdinalIgnoreCase))
            {
                return el;
            }
        }
        return null;
    }

    private static List<IElement> FindDcElements(IDocument doc, string localName)
    {
        var list = new List<IElement>();
        foreach (var el in doc.All)
        {
            if (string.Equals(el.LocalName, localName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(el.TagName, $"dc:{localName}", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(el);
            }
        }
        return list;
    }

    private static Dictionary<string, (string Href, string MediaType, string Properties)> ParseManifest(IDocument opfDoc, string opfDir)
    {
        var map = new Dictionary<string, (string Href, string MediaType, string Properties)>(StringComparer.Ordinal);
        foreach (var item in opfDoc.All)
        {
            if (!string.Equals(item.LocalName, "item", StringComparison.OrdinalIgnoreCase)) continue;
            string? id = item.GetAttribute("id");
            string? href = item.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href)) continue;

            string resolvedHref = ResolvePosixPath(opfDir, Uri.UnescapeDataString(href));
            string mediaType = item.GetAttribute("media-type") ?? "";
            string props = item.GetAttribute("properties") ?? "";
            map[id] = (resolvedHref, mediaType, props);
        }
        return map;
    }

    private static (List<string> SpineHrefs, string WritingMode) ParseSpine(
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

    private static (byte[]? Bytes, string? Extension) ExtractCoverImage(
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

        var entry = GetEntryNormalized(archive, coverHref);
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

    private static Dictionary<string, string> ParseNavigationTitles(
        ZipArchive archive,
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Try EPUB 3 Nav Document (properties contains "nav")
        foreach (var item in manifest.Values)
        {
            if (item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("nav", StringComparer.OrdinalIgnoreCase))
            {
                var navEntry = GetEntryNormalized(archive, item.Href);
                if (navEntry is not null)
                {
                    string navDir = GetPosixDirectory(item.Href);
                    var navDoc = ParseXmlOrHtml(ReadEntryString(navEntry));
                    foreach (var a in navDoc.QuerySelectorAll("nav a[href], a[href]"))
                    {
                        string? rawHref = a.GetAttribute("href");
                        string label = a.TextContent.Trim();
                        if (string.IsNullOrWhiteSpace(rawHref) || string.IsNullOrWhiteSpace(label)) continue;
                        string cleanHref = rawHref.Split('#')[0];
                        if (string.IsNullOrWhiteSpace(cleanHref)) continue;
                        string resolved = ResolvePosixPath(navDir, Uri.UnescapeDataString(cleanHref));
                        map.TryAdd(resolved, label);
                    }
                }
            }
        }

        if (map.Count > 0) return map;

        // 2. Fallback to EPUB 2 NCX
        foreach (var item in manifest.Values)
        {
            if (string.Equals(item.MediaType, "application/x-dtbncx+xml", StringComparison.OrdinalIgnoreCase) ||
                item.Href.EndsWith(".ncx", StringComparison.OrdinalIgnoreCase))
            {
                var ncxEntry = GetEntryNormalized(archive, item.Href);
                if (ncxEntry is not null)
                {
                    string ncxDir = GetPosixDirectory(item.Href);
                    var ncxDoc = ParseXmlOrHtml(ReadEntryString(ncxEntry));
                    foreach (var np in ncxDoc.All)
                    {
                        if (!string.Equals(np.LocalName, "navPoint", StringComparison.OrdinalIgnoreCase)) continue;
                        string? label = null;
                        string? src = null;
                        foreach (var child in np.QuerySelectorAll("*"))
                        {
                            if (label is null && string.Equals(child.LocalName, "text", StringComparison.OrdinalIgnoreCase))
                            {
                                label = child.TextContent.Trim();
                            }
                            else if (src is null && string.Equals(child.LocalName, "content", StringComparison.OrdinalIgnoreCase))
                            {
                                src = child.GetAttribute("src")?.Trim();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(src))
                        {
                            string cleanHref = src.Split('#')[0];
                            string resolved = ResolvePosixPath(ncxDir, Uri.UnescapeDataString(cleanHref));
                            map.TryAdd(resolved, label);
                        }
                    }
                }
            }
        }

        return map;
    }

    private static int CountNormalizedChapterCharacters(ZipArchiveEntry entry)
    {
        var doc = ParseXmlOrHtml(ReadEntryString(entry));
        var body = doc.Body ?? doc.DocumentElement;
        if (body is null) return 0;

        // Exclude <rt> and <rp> ruby readings and <script>/<style> when counting base story characters
        int count = 0;
        CountTextNodeCharsRecursive(body, ref count);
        return count;
    }

    private static void CountTextNodeCharsRecursive(INode node, ref int count)
    {
        if (node is IElement el)
        {
            string local = el.LocalName;
            if (string.Equals(local, "rt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "rp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "style", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "head", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (node is IText textNode)
        {
            foreach (char c in textNode.Data)
            {
                if (!char.IsWhiteSpace(c))
                {
                    count++;
                }
            }
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            CountTextNodeCharsRecursive(child, ref count);
        }
    }

    private static bool IsFrontOrBackMatter(string title, string href)
    {
        string trimmed = title.Trim();
        if (FrontBackMatterTitles.Contains(trimmed)) return true;

        string fileName = Path.GetFileNameWithoutExtension(href).ToLowerInvariant();
        return fileName is "cover" or "toc" or "nav" or "colophon" or "copyright" or "caution";
    }

    private static string? FindOpfPath(ZipArchive archive)
    {
        var containerEntry = GetEntryNormalized(archive, "META-INF/container.xml");
        if (containerEntry is null) return null;

        var doc = ParseXmlOrHtml(ReadEntryString(containerEntry));
        foreach (var el in doc.All)
        {
            if (string.Equals(el.LocalName, "rootfile", StringComparison.OrdinalIgnoreCase))
            {
                string? fullPath = el.GetAttribute("full-path")?.Trim();
                if (!string.IsNullOrWhiteSpace(fullPath))
                {
                    return NormalizePosixPath(fullPath);
                }
            }
        }
        return null;
    }

    private static string? FindFirstOpfEntry(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePosixPath(entry.FullName);
            }
        }
        return null;
    }

    public static ZipArchiveEntry? GetEntryNormalized(ZipArchive archive, string posixPath)
    {
        string target = NormalizePosixPath(posixPath);
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(NormalizePosixPath(entry.FullName), target, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private static string ReadEntryString(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IDocument ParseXmlOrHtml(string markup)
    {
        var htmlParser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = false
        });
        return htmlParser.ParseDocument(markup);
    }

    public static string NormalizePosixPath(string path)
    {
        string p = path.Replace('\\', '/').TrimStart('/');
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(part);
            }
        }
        return string.Join('/', stack);
    }

    public static string GetPosixDirectory(string posixPath)
    {
        string norm = NormalizePosixPath(posixPath);
        int idx = norm.LastIndexOf('/');
        return idx < 0 ? "" : norm[..idx];
    }

    public static string ResolvePosixPath(string baseDir, string relativeHref)
    {
        if (string.IsNullOrEmpty(baseDir))
        {
            return NormalizePosixPath(relativeHref);
        }
        return NormalizePosixPath($"{baseDir}/{relativeHref}");
    }

    public static string NormalizeLanguageTag(string rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage)) return "ja";
        string primary = rawLanguage.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary switch
        {
            "jpn" => "ja",
            "eng" => "en",
            "zho" or "chi" => "zh",
            "kor" => "ko",
            _ => primary
        };
    }
}
