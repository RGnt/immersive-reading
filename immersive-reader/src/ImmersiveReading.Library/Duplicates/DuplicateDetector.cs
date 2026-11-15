using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ImmersiveReading.Library.Duplicates;

/// <summary>
/// Finds groups of likely duplicate books (spec 21). Pure: it works on <see cref="BookFacts"/> and never touches the database.
/// Candidate pairs come from shared blocking keys, so 10,000+ books are not compared pairwise.
/// </summary>
public static class DuplicateDetector
{
    private sealed class Derived
    {
        public required BookFacts Facts { get; init; }
        public required string Base { get; init; }
        public required string Numbers { get; init; }
        /// <summary>Volume read from the title: strong evidence.</summary>
        public double? TitleVolume { get; init; }
        /// <summary>Volume guessed from the file name or the series index: weak evidence (series indexes are often wrong, e.g. 14.5 stored as 14).</summary>
        public double? WeakVolume { get; init; }
        public double? Volume => TitleVolume ?? WeakVolume;
        public required IReadOnlySet<double> NumberValues { get; init; }
        public required string Series { get; init; }
        public required IReadOnlyList<string> Authors { get; init; }
        public string? Isbn { get; init; }
        public required string Identifier { get; init; }
        public required string Language { get; init; }
    }

    public static DuplicateSearchResult Find(IReadOnlyList<BookFacts> books, IReadOnlySet<(string, string)>? dismissedPairs = null, DuplicateOptions? options = null)
    {
        var watch = Stopwatch.StartNew();
        options ??= new DuplicateOptions();
        var items = books.Select(Derive).ToList();

        var idCounts = items.Where(d => d.Identifier.Length > 0).GroupBy(d => d.Identifier).ToDictionary(g => g.Key, g => g.Count());
        bool IdOk(Derived d) => d.Identifier.Length > 0 && idCounts.TryGetValue(d.Identifier, out int n) && n <= options.MaxIdentifierBucket;

        var pairs = new List<(int A, int B, int Score, List<string> Reasons)>();
        var seen = new HashSet<long>();
        foreach (var (a, b) in CandidatePairs(items, options))
        {
            long key = (long)Math.Min(a, b) * items.Count + Math.Max(a, b);
            if (!seen.Add(key)) continue;
            if (dismissedPairs is not null && dismissedPairs.Contains(PairKey(items[a].Facts.BookId, items[b].Facts.BookId))) continue;
            var scored = Score(items[a], items[b], IdOk(items[a]) && IdOk(items[b]));
            if (scored is { } s && s.Score >= options.MinScore) pairs.Add((a, b, s.Score, s.Reasons));
        }

        var groups = Group(items, pairs);
        return new DuplicateSearchResult(books.Count, watch.ElapsedMilliseconds, groups);
    }

    public static (string, string) PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    public static string ConfidenceOf(int score) => score >= 90 ? "certain" : score >= 70 ? "likely" : "possible";

    // ------------------------------------------------------------------ derivation
    private static Derived Derive(BookFacts f)
    {
        var title = DuplicateNormalizer.Title(f.Title);
        double? weak = DuplicateNormalizer.Title(Path.GetFileNameWithoutExtension(f.EpubFileName ?? "")).Volume
            ?? (f.SeriesIndex is { } idx && idx > 0 && Math.Abs(idx - Math.Round(idx)) < 1e-9 ? Math.Round(idx) : null);
        var numberValues = title.Numbers.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
            .Where(d => !double.IsNaN(d)).ToHashSet();
        string? isbn = DuplicateNormalizer.Isbn(f.Isbn) ?? DuplicateNormalizer.Isbn(f.Identifier);
        string identifier = (f.Identifier ?? "").Trim().ToLowerInvariant();
        if (identifier.Length < 8 || identifier is "unknown" or "none" or "null" or "undefined") identifier = "";
        return new Derived
        {
            Facts = f,
            Base = title.Base,
            Numbers = title.Numbers,
            TitleVolume = title.Volume,
            WeakVolume = weak,
            NumberValues = numberValues,
            Series = DuplicateNormalizer.Series(f.SeriesName),
            Authors = DuplicateNormalizer.Authors(f.Authors),
            Isbn = isbn,
            Identifier = identifier,
            Language = (f.Language ?? "").Trim().ToLowerInvariant()
        };
    }

    // ------------------------------------------------------------------ blocking
    private static string VolKey(double? v) => v is null ? "-" : v.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<(int, int)> CandidatePairs(List<Derived> items, DuplicateOptions options)
    {
        var byIsbn = new Dictionary<string, List<int>>();
        var byId = new Dictionary<string, List<int>>();
        var byBase = new Dictionary<string, List<int>>();
        var bySeries = new Dictionary<string, List<int>>();
        var byAuthor = new Dictionary<string, List<int>>();

        static void Add(Dictionary<string, List<int>> map, string key, int i)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(i);
        }

        for (int i = 0; i < items.Count; i++)
        {
            var d = items[i];
            if (d.Isbn is not null) Add(byIsbn, d.Isbn, i);
            if (d.Identifier.Length > 0) Add(byId, d.Identifier, i);
            if (d.Base.Length > 0) Add(byBase, d.Base, i);
            if (d.Series.Length > 0 && d.Facts.SeriesIndex is { } si) Add(bySeries, d.Series + "|" + si.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), i);
            if (d.Base.Length > 0)
                foreach (string author in d.Authors) Add(byAuthor, author + "|" + VolKey(d.Volume), i);
        }

        foreach (var list in byIsbn.Values) foreach (var p in AllPairs(list)) yield return p;
        foreach (var list in byId.Values.Where(l => l.Count <= options.MaxIdentifierBucket)) foreach (var p in AllPairs(list)) yield return p;
        foreach (var list in bySeries.Values) foreach (var p in AllPairs(list)) yield return p;

        // Same base title: only within the same volume, or involving a book whose volume is unknown (the others differ by definition).
        foreach (var list in byBase.Values)
        {
            if (list.Count < 2) continue;
            var unknown = list.Where(i => items[i].Volume is null).ToList();
            foreach (var group in list.Where(i => items[i].Volume is not null).GroupBy(i => items[i].Volume))
                foreach (var p in AllPairs(group.ToList())) yield return p;
            foreach (var p in AllPairs(unknown)) yield return p;
            foreach (int u in unknown)
                foreach (int k in list.Where(i => items[i].Volume is not null)) yield return (u, k);
        }

        // Fuzzy titles: same author name and same volume, bounded bucket size.
        foreach (var list in byAuthor.Values.Where(l => l.Count > 1 && l.Count <= options.MaxFuzzyBucket))
            foreach (var p in AllPairs(list)) yield return p;
    }

    private static IEnumerable<(int, int)> AllPairs(List<int> list)
    {
        for (int x = 0; x < list.Count; x++)
            for (int y = x + 1; y < list.Count; y++)
                yield return (list[x], list[y]);
    }

    // ------------------------------------------------------------------ scoring
    private static (int Score, List<string> Reasons)? Score(Derived a, Derived b, bool identifierUsable)
    {
        if (a.Language.Length > 0 && b.Language.Length > 0 && a.Language != b.Language) return null;        // hard separator
        // Volume agreement. A volume written in a title is strong evidence, one from a file name or series index is weak.
        //   both titles state one: they must agree; one title states it: the other must agree through its weak volume or a number in its
        //   title, or be volume 1 ("Title" and "Title 1"); neither states one: weak volumes must agree when both exist.
        bool volumeBothKnown, volumeBothNone = false, volumeOneSide = false;
        if (a.TitleVolume is { } ta && b.TitleVolume is { } tb)
        {
            if (Math.Abs(ta - tb) > 1e-9) return null;
            volumeBothKnown = true;
        }
        else if (a.TitleVolume is not null || b.TitleVolume is not null)
        {
            var (v, other) = a.TitleVolume is { } av ? (av, b) : (b.TitleVolume!.Value, a);
            if (other.WeakVolume is { } w)
            {
                if (Math.Abs(v - w) > 1e-9) return null;
                volumeBothKnown = true;
            }
            else if (other.NumberValues.Contains(v)) volumeBothKnown = true;
            else if (Math.Abs(v - 1) < 1e-9) { volumeBothKnown = false; volumeOneSide = true; }
            else return null;
        }
        else if (a.WeakVolume is { } wa && b.WeakVolume is { } wb)
        {
            if (Math.Abs(wa - wb) > 1e-9) return null;
            volumeBothKnown = true;
        }
        else
        {
            volumeBothKnown = false;
            volumeOneSide = a.WeakVolume is not null || b.WeakVolume is not null;
            volumeBothNone = !volumeOneSide;
        }
        bool authorsKnown = a.Authors.Count > 0 && b.Authors.Count > 0;
        bool authorsShared = authorsKnown && DuplicateNormalizer.AuthorsShareName(a.Authors, b.Authors);
        bool authorsConflict = authorsKnown && !authorsShared;
        bool baseEqual = a.Base.Length > 0 && a.Base == b.Base;

        int best = 0;
        var reasons = new List<string>();
        void Rule(int score, string reason)
        {
            if (score > best) { best = score; reasons.Clear(); }
            if (score == best) reasons.Add(reason);
        }

        if (a.Isbn is not null && a.Isbn == b.Isbn) Rule(100, "same ISBN");
        // Some publishers and tools stamp one identifier on every volume, so an identifier alone proves nothing: the titles must agree too.
        if (a.Identifier.Length > 0 && a.Identifier == b.Identifier && identifierUsable && a.Numbers == b.Numbers && DuplicateNormalizer.Dice(a.Base, b.Base) >= 0.85)
            Rule(95, "same publication identifier");

        if (baseEqual && !authorsConflict)
        {
            if (!volumeOneSide)
            {
                if (authorsShared) Rule(90, "same title" + (volumeBothKnown ? " and volume" : "") + ", same author");
                else Rule(60, "same title" + (volumeBothKnown ? " and volume" : "") + ", author unknown on one side");
            }
            else if (authorsShared) Rule(80, "same title, volume known on one side only, same author");
        }

        if (a.Series.Length > 0 && a.Series == b.Series && a.Facts.SeriesIndex is { } ia && b.Facts.SeriesIndex is { } ib
            && Math.Abs(ia - ib) < 1e-9 && !authorsConflict && a.Numbers == b.Numbers)
        {
            double dice = DuplicateNormalizer.Dice(a.Base, b.Base);
            if (dice >= 0.6) Rule(80, "same series and volume, similar title");
        }

        if (!baseEqual && !volumeOneSide && authorsShared && a.Base.Length > 0 && b.Base.Length > 0 && a.Numbers == b.Numbers)
        {
            double dice = DuplicateNormalizer.Dice(a.Base, b.Base);
            if (dice >= 0.88) Rule(70, $"very similar title ({dice:P0}), same author");
        }

        if (best == 0) return null;

        int adjusted = best;
        if (a.Facts.StatsComputed && b.Facts.StatsComputed && a.Facts.TotalChars > 0 && b.Facts.TotalChars > 0)
        {
            double diff = Math.Abs(a.Facts.TotalChars - b.Facts.TotalChars) / (double)Math.Max(a.Facts.TotalChars, b.Facts.TotalChars);
            if (diff <= 0.03) { adjusted = Math.Min(100, best + 10); reasons.Add("same length"); }
            else if (diff > 0.25) { adjusted = best - 15; reasons.Add($"very different length ({diff:P0}): abridged or another edition?"); }
        }
        return (adjusted, reasons);
    }

    // ------------------------------------------------------------------ grouping
    private static List<DuplicateGroup> Group(List<Derived> items, List<(int A, int B, int Score, List<string> Reasons)> pairs)
    {
        int n = items.Count;
        int[] parent = Enumerable.Range(0, n).ToArray();
        var volumes = new HashSet<double>?[n];
        for (int i = 0; i < n; i++) if (items[i].Volume is { } v) volumes[i] = [v];

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        foreach (var pair in pairs.OrderByDescending(p => p.Score))
        {
            int ra = Find(pair.A), rb = Find(pair.B);
            if (ra == rb) continue;
            var merged = new HashSet<double>();
            if (volumes[ra] is not null) merged.UnionWith(volumes[ra]!);
            if (volumes[rb] is not null) merged.UnionWith(volumes[rb]!);
            if (merged.Count > 1) continue;                 // never join two different volumes through a chain
            parent[rb] = ra;
            volumes[ra] = merged.Count > 0 ? merged : null;
        }

        var members = new Dictionary<int, List<int>>();
        var touched = new HashSet<int>(pairs.SelectMany(p => new[] { p.A, p.B }));
        foreach (int i in touched)
        {
            int root = Find(i);
            if (!members.TryGetValue(root, out var list)) members[root] = list = [];
            list.Add(i);
        }

        var result = new List<DuplicateGroup>();
        foreach (var list in members.Values.Where(l => l.Count > 1))
        {
            var set = list.ToHashSet();
            var groupPairs = pairs.Where(p => set.Contains(p.A) && set.Contains(p.B) && Find(p.A) == Find(p.B)).ToList();
            if (groupPairs.Count == 0) continue;

            var ids = list.Select(i => items[i].Facts.BookId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            string groupId = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(string.Join("|", ids))))[..12].ToLowerInvariant();
            int top = groupPairs.Max(p => p.Score);

            var memberList = RankWithinGroup(list.Select(i =>
            {
                var (keep, why) = KeepScore(items[i].Facts);
                int bestMatch = groupPairs.Where(p => p.A == i || p.B == i).Select(p => p.Score).DefaultIfEmpty(0).Max();
                return new DuplicateMember(items[i].Facts, keep, why, bestMatch);
            }).ToList())
            .OrderByDescending(m => m.KeepScore)
            .ThenBy(m => m.Book.AddedAtUtc, StringComparer.Ordinal)
            .ThenBy(m => m.Book.BookId, StringComparer.Ordinal)
            .ToList();

            result.Add(new DuplicateGroup(
                groupId, ConfidenceOf(top), top, memberList[0].Book.BookId, memberList,
                groupPairs.OrderByDescending(p => p.Score)
                    .Select(p => new DuplicatePair(items[p.A].Facts.BookId, items[p.B].Facts.BookId, p.Score, p.Reasons)).ToList()));
        }

        return result
            .OrderByDescending(g => g.Score)
            .ThenByDescending(g => g.Members.Count)
            .ThenBy(g => g.Members[0].Book.Title, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ keeper recommendation
    internal static (int Score, IReadOnlyList<string> Reasons) KeepScore(BookFacts f)
    {
        int score = 0;
        var why = new List<string>();
        if (f.ProgressPercent > 0) { score += 40; why.Add($"has {f.ProgressPercent:0}% reading progress"); }
        if (f.HasStudyPack) { score += 15; why.Add("has a study pack"); }
        if (f.HasDeckCsv) { score += 15; why.Add("has a vocabulary deck"); }

        int complete = 0;
        if (!string.IsNullOrWhiteSpace(f.SeriesName)) complete++;
        if (!string.IsNullOrWhiteSpace(f.Authors)) complete++;
        if (!string.IsNullOrWhiteSpace(f.Isbn)) complete++;
        if (!string.IsNullOrWhiteSpace(f.Description)) complete++;
        if (f.HasCover) complete++;
        score += complete * 3;
        if (complete >= 4) why.Add("complete metadata");
        if (f.TagCount > 0) { score += 2; why.Add($"{f.TagCount} tag(s)"); }
        return (score, why);
    }

    /// <summary>Tie-breakers that need the whole group (longest text, biggest file) are applied by ordering in <see cref="Group"/>.</summary>
    internal static IReadOnlyList<DuplicateMember> RankWithinGroup(IReadOnlyList<DuplicateMember> members)
    {
        long maxChars = members.Max(m => (long)m.Book.TotalChars);
        long maxSize = members.Max(m => m.Book.FileSize);
        return members.Select(m =>
        {
            int score = m.KeepScore;
            var why = m.KeepReasons.ToList();
            if (members.Count > 1 && maxChars > 0 && m.Book.StatsComputed && m.Book.TotalChars == maxChars && members.Any(o => o.Book.TotalChars < maxChars)) { score += 5; why.Add("longest text"); }
            if (members.Count > 1 && maxSize > 0 && m.Book.FileSize == maxSize && members.Any(o => o.Book.FileSize < maxSize)) { score += 2; why.Add("largest file"); }
            return m with { KeepScore = score, KeepReasons = why };
        }).ToList();
    }
}
