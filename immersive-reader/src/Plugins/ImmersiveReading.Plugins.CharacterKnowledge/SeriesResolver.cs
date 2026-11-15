using System.Security.Cryptography;
using System.Text;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <summary>
/// Maps library books to knowledge-graph positions (spec 18, section 3): a standalone book is volume 1 of its own series; the
/// volumes of a series are numbered 1..n in reading order (<c>SeriesIndex</c>, then title). The numbering a knowledge base was
/// built with is remembered (<paramref name="storedMap"/>), so adding a volume later does not renumber the old ones; if a new
/// volume sorts before an already-built one the order has changed and <see cref="SeriesInfo.OrderChanged"/> says a rebuild is needed.
/// </summary>
public sealed class SeriesResolver(IHostLibrary library, Func<string, IReadOnlyDictionary<string, int>?>? storedMap = null)
{
    public static string KeyForSeries(string seriesName) => "series-" + Hash(seriesName.Trim().ToLowerInvariant());
    public static string KeyForBook(string bookId) => "book-" + Hash(bookId);

    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    public SeriesInfo ResolveForBook(string bookId)
    {
        var summary = library.GetBookDetail(bookId)?.Summary ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");
        return string.IsNullOrWhiteSpace(summary.SeriesName)
            ? Number(KeyForBook(bookId), summary.Title, isSeries: false, [summary])
            : ResolveForSeries(summary.SeriesName);
    }

    public SeriesInfo ResolveForSeries(string seriesName)
    {
        string name = seriesName.Trim();
        var books = library.ListBooks(new BookListQuery(SeriesName: name, Sort: "series_or_title", Limit: 1000)).Items
            .Where(b => string.Equals(b.SeriesName?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (books.Count == 0) throw new KeyNotFoundException($"No books found for the series '{name}'.");
        return Number(KeyForSeries(name), name, isSeries: true, books);
    }

    private SeriesInfo Number(string key, string title, bool isSeries, IReadOnlyList<BookSummary> books)
    {
        var sorted = books
            .OrderBy(b => b.SeriesIndex ?? double.MaxValue)
            .ThenBy(b => b.SortTitle, StringComparer.Ordinal)
            .ThenBy(b => b.BookId, StringComparer.Ordinal)
            .ToList();
        var stored = storedMap?.Invoke(key);

        var ordinals = new Dictionary<string, int>();
        bool orderChanged = false;
        if (stored is null || stored.Count == 0)
        {
            for (int i = 0; i < sorted.Count; i++) ordinals[sorted[i].BookId] = i + 1;
        }
        else
        {
            int next = stored.Values.Max() + 1;
            int lastMapped = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                var book = sorted[i];
                if (stored.TryGetValue(book.BookId, out int ordinal))
                {
                    if (ordinal < lastMapped) orderChanged = true;     // known volumes now sort in a different order
                    ordinals[book.BookId] = ordinal;
                    lastMapped = Math.Max(lastMapped, ordinal);
                }
                else
                {
                    // a new volume: fine after everything built so far, a changed order if it sorts before a built volume
                    if (sorted.Skip(i + 1).Any(later => stored.ContainsKey(later.BookId))) orderChanged = true;
                    ordinals[book.BookId] = next++;
                }
            }
        }

        var volumes = sorted
            .Select(b => new VolumeInfo(ordinals[b.BookId], b.BookId, b.Title, b.SeriesIndex, b.ChapterCount))
            .OrderBy(v => v.Volume)
            .ToList();
        return new SeriesInfo(key, title, isSeries, volumes, orderChanged);
    }
}
