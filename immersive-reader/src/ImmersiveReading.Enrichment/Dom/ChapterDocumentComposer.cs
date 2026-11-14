using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ImmersiveReading.Core.Epub;

namespace ImmersiveReading.Enrichment.Dom;

/// <summary>One spine file belonging to a chapter: its archive path and raw XHTML.</summary>
public sealed record ChapterMemberSource(string Href, string RawXhtml);

/// <summary>
/// Merges the spine files of one chapter into a single document (spec 12 FR-7).
/// Each member's relative asset/link URLs are resolved against <i>its own</i> path first, then its body is
/// appended in reading order inside <c>&lt;section class="ir-spine-part" data-ir-spine-href="..."&gt;</c>
/// (a section, so block discovery descends through it exactly as it does through body).
/// Images are marked <c>loading="lazy"</c>. Head stylesheets/styles of later members are added to the first member's head when not already present.
/// </summary>
public static class ChapterDocumentComposer
{
    public const string PartClass = "ir-spine-part";

    public static string Compose(string bookId, IReadOnlyList<ChapterMemberSource> members)
    {
        if (members.Count == 0) throw new ArgumentException("A chapter needs at least one member.", nameof(members));

        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = false });
        var documents = new List<(string Href, IDocument Doc)>(members.Count);
        foreach (var member in members)
        {
            var doc = parser.ParseDocument(EpubArchiveReader.NormalizeXhtmlSelfClosingTags(member.RawXhtml));
            EpubUrlRewriter.RewriteRelativeAssetUrls(doc, bookId, member.Href);
            // A merged chapter can hold hundreds of image pages (image-only books); let the browser
            // fetch them as they scroll into view instead of all at once.
            foreach (var img in doc.QuerySelectorAll("img"))
            {
                if (!img.HasAttribute("loading")) img.SetAttribute("loading", "lazy");
            }
            documents.Add((member.Href, doc));
        }

        var main = documents[0].Doc;
        var mainBody = main.Body ?? throw new InvalidOperationException("Chapter document has no body.");
        var knownHeadKeys = new HashSet<string>(StringComparer.Ordinal);
        if (main.Head is not null)
        {
            foreach (var node in main.Head.Children) knownHeadKeys.Add(HeadKey(node));
        }

        // Wrap the first member's own body content too, so every member is delimited the same way.
        var firstChildren = mainBody.ChildNodes.ToList();
        var firstSection = CreatePart(main, documents[0].Href, mainBody);
        foreach (var child in firstChildren) firstSection.AppendChild(child);
        mainBody.AppendChild(firstSection);

        for (int i = 1; i < documents.Count; i++)
        {
            var (href, doc) = documents[i];

            if (main.Head is not null && doc.Head is not null)
            {
                foreach (var node in doc.Head.Children.ToList())
                {
                    if (!IsStyleResource(node) || !knownHeadKeys.Add(HeadKey(node))) continue;
                    main.Head.AppendChild(main.Import(node, deep: true));
                }
            }

            var body = doc.Body;
            var section = CreatePart(main, href, body);
            if (body is not null)
            {
                foreach (var child in body.ChildNodes.ToList())
                {
                    section.AppendChild(main.Import(child, deep: true));
                }
            }
            mainBody.AppendChild(section);
        }

        return main.DocumentElement.OuterHtml;
    }

    private static IElement CreatePart(IDocument owner, string href, IElement? sourceBody)
    {
        var section = owner.CreateElement("section");
        string bodyClass = sourceBody?.GetAttribute("class") ?? "";
        section.SetAttribute("class", string.IsNullOrWhiteSpace(bodyClass) ? PartClass : $"{PartClass} {bodyClass}");
        section.SetAttribute("data-ir-spine-href", href);
        string? style = sourceBody?.GetAttribute("style");
        if (!string.IsNullOrWhiteSpace(style)) section.SetAttribute("style", style);
        return section;
    }

    private static bool IsStyleResource(IElement node) =>
        node.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase) ||
        (node.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase) &&
         (node.GetAttribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase));

    private static string HeadKey(IElement node) =>
        node.LocalName.Equals("link", StringComparison.OrdinalIgnoreCase)
            ? "link:" + node.GetAttribute("href")
            : node.LocalName + ":" + node.TextContent;
}
