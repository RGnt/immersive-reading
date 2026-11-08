using System.IO.Compression;
using System.Text;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed class TestEpubBuilder
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);

    public TestEpubBuilder()
    {
        AddEntry("mimetype", "application/epub+zip", isCompressed: false);
        AddEntry("META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);
    }

    public TestEpubBuilder AddEntry(string entryPath, string textContent, bool isCompressed = true)
    {
        return AddEntry(entryPath, Encoding.UTF8.GetBytes(textContent), isCompressed);
    }

    public TestEpubBuilder AddEntry(string entryPath, byte[] data, bool isCompressed = true)
    {
        _entries[entryPath] = data;
        return this;
    }

    public TestEpubBuilder WithOpf(string opfXml)
    {
        return AddEntry("OEBPS/content.opf", opfXml);
    }

    public TestEpubBuilder WithChapter(string relativePath, string title, string bodyHtml)
    {
        string fullXhtml = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>{{title}}</title></head>
              <body>
                {{bodyHtml}}
              </body>
            </html>
            """;
        return AddEntry($"OEBPS/{relativePath}", fullXhtml);
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kvp in _entries)
            {
                var entry = zip.CreateEntry(kvp.Key, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(kvp.Value, 0, kvp.Value.Length);
            }
        }
        return ms.ToArray();
    }
}

