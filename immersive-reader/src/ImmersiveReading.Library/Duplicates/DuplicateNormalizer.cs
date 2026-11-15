using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Library.Duplicates;

/// <summary>Normalization used to compare titles, authors and ISBNs (spec 21 section 3.1). Pure functions, no I/O.</summary>
public static class DuplicateNormalizer
{
    /// <param name="Base">Title without edition noise and volume markers, letters and digits only, case- and kana-folded.</param>
    /// <param name="Volume">Volume found in the title: a number, 1/2/3 for 上/中/下, or number + part/10 for "II 上" (= 2.1); null when none.</param>
    /// <param name="Numbers">The digit runs and roman numerals that remain in the title: two titles with different numbers are different books.</param>
    public sealed record TitleKey(string Base, double? Volume, string Numbers = "");

    private static readonly Regex EditionNoise = new(
        @"電子書籍版|電子書籍|電子版|デジタル版|カラー版|フルカラー版?|新装版|改訂版|特装版|限定版|文庫版|kindle\s*edition|kindle版|e-?book",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "(電撃文庫)", "(MF文庫J)", "(GA文庫)", "(Kadokawa Books)": the publisher's label is not part of the title.
    private static readonly Regex Imprint = new(
        @"[\(\[（【][^\)\]）】]*(?:文庫|ノベルズ|ブックス|ノベル|コミックス|レーベル|books?|bunko|novels?|comics?)[^\)\]）】]*[\)\]）】]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Edition remarks at the very end: "【電子特典付き】", "【電子書籍限定書き下ろしSS付き】". They hide the volume number in front of them.
    private static readonly Regex TrailingBracket = new(@"\s*【[^】]*】\s*$", RegexOptions.Compiled);

    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]|【\s*】|「\s*」|『\s*』|〈\s*〉|《\s*》|<\s*>", RegexOptions.Compiled);

    private const string KanjiDigits = "一二三四五六七八九十百壱弐参";

    // 上/中/下 (first/middle/last part of a volume): "<上>", "(下)", " 上巻", " 中"
    private static readonly Regex[] PartPatterns =
    [
        new(@"[<\(\[]\s*([上中下])\s*巻?\s*[>\)\]]", RegexOptions.Compiled),
        new(@"(?:^|\s)([上中下])巻?\s*$", RegexOptions.Compiled),
    ];

    private static readonly Dictionary<string, int> Roman = new()
    {
        ["I"] = 1, ["II"] = 2, ["III"] = 3, ["IV"] = 4, ["V"] = 5, ["VI"] = 6, ["VII"] = 7, ["VIII"] = 8, ["IX"] = 9, ["X"] = 10, ["XI"] = 11, ["XII"] = 12,
    };

    // Most specific first; the first match is the volume and is removed from the title.
    private static readonly (Regex Pattern, Func<Match, double?> Value)[] VolumePatterns =
    [
        (new Regex(@"(?<![0-9.])([0-9]{1,3}\.[0-9]{1,2})\s*(?:巻)?\s*$", RegexOptions.Compiled), m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)),
        (new Regex($@"その\s*([0-9]+|[{KanjiDigits}]+)\s*$", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex($@"第\s*([0-9]+|[{KanjiDigits}]+)\s*(?:巻|冊)", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex($@"([0-9]+|[{KanjiDigits}]+)\s*(?:巻|冊)", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex(@"(?<![a-z])vol(?:ume)?\.?\s*([0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex(@"(?<![a-z0-9])v\.?\s*([0-9]+)(?![0-9a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex(@"#\s*([0-9]+)", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        (new Regex(@"[\(\[]\s*([0-9]+)\s*[\)\]]\s*$", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        // a number at the very end, with or without a space: "Title 3", "Title3", "Title003"
        (new Regex(@"(?<![0-9.])([0-9]{1,3})\s*$", RegexOptions.Compiled), m => Number(m.Groups[1].Value)),
        // a roman numeral at the end: "Title VI", "ホライゾンII"
        (new Regex(@"(?<![A-Za-z])(XII|XI|X|IX|VIII|VII|VI|V|IV|III|II|I)\s*$", RegexOptions.Compiled), m => Roman[m.Groups[1].Value]),
        // a kanji numeral closing a subtitle or standing alone at the end: "～十一", " 三"
        (new Regex($@"(?<=[~～―\-\)>\s])([{KanjiDigits}]{{1,3}})\s*$", RegexOptions.Compiled), m => KanjiNumber(m.Groups[1].Value)),
    ];

    private static readonly Regex NumberTokens = new(@"[0-9]+|#[A-Za-z]+|(?<![A-Za-z])(?:XII|XI|X|IX|VIII|VII|VI|V|IV|III|II|I)(?![A-Za-z])", RegexOptions.Compiled);

    public static TitleKey Title(string? title)
    {
        string s = (title ?? "").Normalize(NormalizationForm.FormKC);
        s = EditionNoise.Replace(s, " ");
        s = Imprint.Replace(s, " ");
        while (TrailingBracket.IsMatch(s)) s = TrailingBracket.Replace(s, "");
        s = EmptyBrackets.Replace(s, " ").Trim();

        double? part = null;
        foreach (var pattern in PartPatterns)
        {
            var m = pattern.Match(s);
            if (!m.Success) continue;
            part = HalfNumber(m.Groups[1].Value);
            s = s.Remove(m.Index, m.Length);
            break;
        }

        double? number = null;
        foreach (var (pattern, value) in VolumePatterns)
        {
            var m = pattern.Match(s);
            if (!m.Success) continue;
            number = value(m);
            if (number is null) continue;
            s = s.Remove(m.Index, m.Length);
            break;
        }

        double? volume = (number, part) switch
        {
            ({ } n, { } p) => n + p / 10,
            ({ } n, null) => n,
            (null, { } p) => p,
            _ => null
        };
        s = EmptyBrackets.Replace(s, " ");
        string numbers = string.Join(",", NumberTokens.Matches(s).Select(m => m.Value));
        return new TitleKey(Fold(s), volume, numbers);
    }

    /// <summary>Series name folded for comparison; empty when there is none.</summary>
    public static string Series(string? series) => Fold((series ?? "").Normalize(NormalizationForm.FormKC));

    private static readonly Regex AuthorSplit = new(@"[、,，/／;；&＆·|｜]|\s+と\s+|\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Parenthetical = new(@"\([^)]*\)|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex RoleSuffix = new(@"\s*(?:原作|著者|著|作|イラスト|画|絵|訳|編|監修)\s*$", RegexOptions.Compiled);

    /// <summary>The distinct normalized author names of an authors string (empty when unknown).</summary>
    public static IReadOnlyList<string> Authors(string? authors)
    {
        string s = (authors ?? "").Normalize(NormalizationForm.FormKC);
        var names = new List<string>();
        foreach (string part in AuthorSplit.Split(s))
        {
            string name = Parenthetical.Replace(part, " ");
            name = RoleSuffix.Replace(name.Trim(), "");
            name = Fold(name);
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Share a name, or one name contains the other (a surname only, at least 2 characters).</summary>
    public static bool AuthorsShareName(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        foreach (string x in a)
            foreach (string y in b)
            {
                if (x == y) return true;
                if (x.Length >= 2 && y.Length >= 2 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal))) return true;
            }
        return false;
    }

    /// <summary>ISBN-13 digits, or null. ISBN-10 is converted; other values (a UUID, a partial number) give null.</summary>
    public static string? Isbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        foreach (char c in raw.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsAsciiDigit(c) || ((c == 'X' || c == 'x') && sb.Length == 9)) sb.Append(char.ToUpperInvariant(c));
        }
        string digits = sb.ToString();
        if (digits.Length == 13 && (digits.StartsWith("978") || digits.StartsWith("979"))) return digits;
        if (digits.Length != 10) return null;

        string core = "978" + digits[..9];
        int sum = 0;
        for (int i = 0; i < 12; i++) sum += (core[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return core + ((10 - sum % 10) % 10);
    }

    /// <summary>Dice coefficient over character bigrams (unigrams for one-character strings).</summary>
    public static double Dice(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var ga = Grams(a);
        var gb = Grams(b);
        int shared = 0;
        foreach (int g in ga) if (gb.Contains(g)) shared++;
        return 2.0 * shared / (ga.Count + gb.Count);
    }

    private static HashSet<int> Grams(string s)
    {
        var set = new HashSet<int>();
        if (s.Length == 1) { set.Add(s[0]); return set; }
        for (int i = 0; i + 1 < s.Length; i++) set.Add((s[i] << 16) | s[i + 1]);
        return set;
    }

    /// <summary>Keep letters and digits only, lower-cased, katakana folded to hiragana.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char raw in s.ToLowerInvariant())
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(raw);
            bool keep = cat is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.DecimalDigitNumber;
            if (!keep) continue;
            sb.Append(raw is >= 'ァ' and <= 'ヶ' ? (char)(raw - 0x60) : raw);
        }
        return sb.ToString();
    }

    private static double? Number(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return n;
        return KanjiNumber(text);
    }

    private static double? HalfNumber(string text) => text switch { "上" => 1, "中" => 2, "下" => 3, _ => null };

    private static double? KanjiNumber(string text)
    {
        int total = 0, current = 0;
        foreach (char c in text)
        {
            int digit = c switch { '壱' => 0, '弐' => 1, '参' => 2, _ => KanjiDigits.IndexOf(c) };
            if (digit < 0) return null;
            if (c == '十') { total += (current == 0 ? 1 : current) * 10; current = 0; }
            else if (c == '百') { total += (current == 0 ? 1 : current) * 100; current = 0; }
            else current = digit + 1;
        }
        return total + current;
    }
}
