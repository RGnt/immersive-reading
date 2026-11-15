using System.Globalization;
using System.Text;
using ImmersiveReading.Library.Duplicates;

namespace ImmersiveReading.Library.Books;

/// <summary>What the numbering needs to know about one selected book.</summary>
public sealed record NumberingInput(string BookId, string Title, string SortTitle, string FileName);

/// <summary>
/// Volume numbers for a bulk edit ("put these 12 books into series X as volumes 1 to 12"). The same code produces the preview and the
/// real update, so what the dialog shows is exactly what is written.
/// </summary>
public static class BulkNumbering
{
    /// <summary>Numbers follow the order the books were selected in.</summary>
    public const string Selection = "selection";
    /// <summary>Numbers follow the natural order of the titles (…2, …10, not …10, …2).</summary>
    public const string Title = "title";
    /// <summary>Numbers follow the natural order of the file names.</summary>
    public const string FileName = "filename";
    /// <summary>Each book gets the volume number found in its title (第3巻, 3, III, 上/中/下 …); books without one keep their number.</summary>
    public const string FromTitle = "fromTitle";

    public static readonly string[] Modes = [Selection, Title, FileName, FromTitle];

    public static string? EffectiveMode(string? mode, bool legacyAutoNumber)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!Modes.Contains(mode)) throw new ArgumentException($"Unknown numbering mode '{mode}' (one of {string.Join(", ", Modes)}).");
            return mode;
        }
        return legacyAutoNumber ? Selection : null;
    }

    /// <summary>Book id → new series index, only for the books that get one.</summary>
    public static Dictionary<string, double> Compute(IReadOnlyList<NumberingInput> books, string? mode, double start, double step)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (mode is null) return result;

        if (mode == FromTitle)
        {
            foreach (var book in books)
            {
                double? volume = DuplicateNormalizer.Title(book.Title).Volume ?? DuplicateNormalizer.Title(Path.GetFileNameWithoutExtension(book.FileName)).Volume;
                if (volume is { } v) result[book.BookId] = v;
            }
            return result;
        }

        IEnumerable<NumberingInput> ordered = mode switch
        {
            Title => books.OrderBy(b => string.IsNullOrWhiteSpace(b.SortTitle) ? b.Title : b.SortTitle, NaturalComparer.Instance).ThenBy(b => b.BookId, StringComparer.Ordinal),
            FileName => books.OrderBy(b => b.FileName, NaturalComparer.Instance).ThenBy(b => b.BookId, StringComparer.Ordinal),
            _ => books
        };
        int i = 0;
        foreach (var book in ordered)
        {
            if (!result.ContainsKey(book.BookId)) result[book.BookId] = Math.Round(start + i++ * step, 4);
        }
        return result;
    }
}

/// <summary>Compares strings so that digit runs compare as numbers ("2" before "10"); full-width digits count as digits.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        var a = Chunks(x ?? "");
        var b = Chunks(y ?? "");
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var (ta, na) = a[i];
            var (tb, nb) = b[i];
            int c = na is not null && nb is not null ? na.Value.CompareTo(nb.Value) : string.CompareOrdinal(ta, tb);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    private static List<(string Text, decimal? Number)> Chunks(string s)
    {
        s = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var list = new List<(string, decimal?)>();
        var sb = new StringBuilder();
        bool digits = false;
        void Flush()
        {
            if (sb.Length == 0) return;
            string text = sb.ToString();
            list.Add((text, digits && decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null));
            sb.Clear();
        }
        foreach (char c in s)
        {
            bool isDigit = c is >= '0' and <= '9';
            if (sb.Length > 0 && isDigit != digits) Flush();
            digits = isDigit;
            sb.Append(c);
        }
        Flush();
        return list;
    }
}
