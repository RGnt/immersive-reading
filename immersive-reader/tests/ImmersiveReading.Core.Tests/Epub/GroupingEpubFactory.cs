using System.Text;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed record GroupingPage(string Id, string Href, string Body, string? TocTitle = null);

/// <summary>Builds small synthetic EPUBs (nav ToC + chosen spine) for chapter grouping tests.</summary>
public static class GroupingEpubFactory
{
    public const string Text60 =
        "これは物語の本文です。冒険がここから始まる。森の奥には不思議な泉があり、少年はそこで不思議な少女に出会った。";

    public static GroupingPage Text(string href, string? toc = null) =>
        new(href.Replace('/', '_'), href, $"<p>{Text60}</p>", toc);

    public static GroupingPage Illustration(string href) =>
        new(href.Replace('/', '_'), href, "<div><img src=\"../images/pic.jpg\" alt=\"\"/></div>");

    public static byte[] BuildBytes(IReadOnlyList<GroupingPage> spine, bool withToc = true, string title = "Grouping Test")
    {
        var manifest = new StringBuilder();
        var itemrefs = new StringBuilder();
        foreach (var p in spine)
        {
            manifest.AppendLine($"""<item id="{p.Id}" href="{p.Href}" media-type="application/xhtml+xml"/>""");
            itemrefs.AppendLine($"""<itemref idref="{p.Id}"/>""");
        }

        var nav = new StringBuilder();
        foreach (var p in spine.Where(p => p.TocTitle is not null))
        {
            // nav.xhtml lives in text/, so hrefs are relative to text/
            nav.AppendLine($"""<li><a href="{p.Href.Replace("text/", "")}">{p.TocTitle}</a></li>""");
        }

        string opf = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{title}</dc:title>
                <dc:language>ja</dc:language>
                <dc:identifier id="pub-id">urn:uuid:{Guid.NewGuid()}</dc:identifier>
              </metadata>
              <manifest>
                {(withToc ? """<item id="nav" href="text/nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>""" : "")}
                {manifest}
              </manifest>
              <spine>
                {itemrefs}
              </spine>
            </package>
            """;

        var builder = new TestEpubBuilder().WithOpf(opf);
        if (withToc)
        {
            builder.WithChapter("text/nav.xhtml", "nav", $"""
                <nav xmlns:epub="http://www.idpf.org/2007/ops" epub:type="toc"><ol>{nav}</ol></nav>
                """);
        }
        foreach (var p in spine)
        {
            builder.WithChapter(p.Href, p.Id, p.Body);
        }

        return builder.Build();
    }

    public static ExtractedEpubPackage Extract(IReadOnlyList<GroupingPage> spine, bool deepStats = false, bool withToc = true)
    {
        using var ms = new MemoryStream(BuildBytes(spine, withToc));
        return EpubMetadataExtractor.ExtractFromStream(ms, "grouping.epub", computeDeepChapterStats: deepStats);
    }
}
