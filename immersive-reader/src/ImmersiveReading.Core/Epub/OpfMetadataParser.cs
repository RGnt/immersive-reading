using System.Globalization;
using System.Xml.Linq;
using AngleSharp.Dom;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

internal static class OpfMetadataParser
{
    public static OpfOriginalMetadata ParseOpfMetadataXml(string opfXml, IDocument fallbackOpfDoc)
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
            string language = EpubLanguage.NormalizeLanguageTag(rawLang);
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

    public static OpfOriginalMetadata ParseOpfMetadata(IDocument opfDoc)
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
            string val = !string.IsNullOrWhiteSpace(meta.TextContent) ? meta.TextContent.Trim() : (content ?? "");

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
        string language = EpubLanguage.NormalizeLanguageTag(rawLang);
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

    public static string? GetFileAs(IElement element, Dictionary<string, List<(string Property, string Value)>> refinesMap)
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

    public static IElement? FindDcElement(IDocument doc, string localName)
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

    public static List<IElement> FindDcElements(IDocument doc, string localName)
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
}

