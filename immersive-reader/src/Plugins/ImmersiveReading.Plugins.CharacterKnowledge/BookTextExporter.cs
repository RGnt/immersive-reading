using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Epub;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <param name="Json">The <c>book-text.json</c> document (spec 18, contract 6.1).</param>
/// <param name="ChapterHashes">(volume, chapter) → SHA-256 of the exported text, the builder's staleness check.</param>
public sealed record ExportedBook(JsonObject Json, int Chapters, long Characters, IReadOnlyDictionary<(int Volume, int Chapter), string> ChapterHashes);

/// <summary>
/// Writes the book text the builder reads: per volume, per library chapter, the plain visible text (ruby readings removed, one
/// line per paragraph). Each spine file belongs to the first chapter that references it, as in the deck builder, so a chapter is
/// never counted twice and the numbering matches the reader's chapters (<c>book_chapters.chapter_index</c>).
/// </summary>
public sealed class BookTextExporter(IBookCatalog catalog)
{
    public ExportedBook Export(SeriesInfo series, int uptoVolume = int.MaxValue)
    {
        var volumes = new JsonArray();
        var hashes = new Dictionary<(int, int), string>();
        int chapterCount = 0;
        long characters = 0;

        foreach (var volume in series.Volumes.Where(v => v.Volume <= uptoVolume).OrderBy(v => v.Volume))
        {
            var detail = catalog.GetBookDetail(volume.BookId) ?? throw new KeyNotFoundException($"Book '{volume.BookId}' not found.");
            string epub = catalog.GetEpubFilePath(volume.BookId) ?? throw new FileNotFoundException($"The EPUB of '{volume.Title}' is not available.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var chapters = new JsonArray();

            foreach (var chapter in detail.Chapters.Where(c => !c.IsExcluded).OrderBy(c => c.ChapterIndex))
            {
                var hrefs = chapter.Members.Where(seen.Add).ToList();
                var paragraphs = hrefs.Count == 0 ? [] : BookTextExtractor.ExtractParagraphs(epub, hrefs);
                string text = paragraphs.Count == 0 ? "" : string.Join("\n", paragraphs) + "\n";
                chapters.Add(new JsonObject { ["index"] = chapter.ChapterIndex, ["title"] = chapter.Title, ["text"] = text });
                hashes[(volume.Volume, chapter.ChapterIndex)] = HashOf(text);
                chapterCount++;
                characters += text.Length;
            }

            volumes.Add(new JsonObject
            {
                ["volume"] = volume.Volume,
                ["book_id"] = volume.BookId,
                ["title"] = volume.Title,
                ["series_index"] = volume.SeriesIndex,
                ["chapters"] = chapters
            });
        }

        var json = new JsonObject
        {
            ["series_key"] = series.SeriesKey,
            ["title"] = series.Title,
            ["language"] = "ja",
            ["volumes"] = volumes
        };
        return new ExportedBook(json, chapterCount, characters, hashes);
    }

    /// <summary>Same as the builder's <c>text_sha</c>: SHA-256 of the UTF-8 text, lowercase hex.</summary>
    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
