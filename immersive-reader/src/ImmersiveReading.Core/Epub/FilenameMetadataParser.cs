using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Core.Epub;

public sealed record FilenameParsedHints(
    string CleanTitle,
    string Authors,
    string Imprint,
    string SeriesName,
    double? SeriesIndex,
    string Isbn
);

/// <summary>
/// Extracts fallback bibliographic hints from common Japanese & standard ebook filename conventions:
/// - "[Author] Title Vol.01 (Imprint).epub"
/// - "Title (Imprint) - Author.epub"
/// - "Series Name 第02巻.epub" / "Series Name ２.epub"
/// </summary>
public static partial class FilenameMetadataParser
{
    [GeneratedRegex(@"^\s*\[(?<author>[^\]]+)\]\s*(?<rest>.+)$", RegexOptions.Compiled)]
    private static partial Regex LeadingBracketAuthorRegex();

    [GeneratedRegex(@"\((?<paren>[^()]+)\)", RegexOptions.Compiled)]
    private static partial Regex ParenthesizedTokenRegex();

    [GeneratedRegex(@"\s+-\s+(?<author>[^-\[\]()]+)\s*$", RegexOptions.Compiled)]
    private static partial Regex TrailingDashAuthorRegex();

    [GeneratedRegex(@"(?:第\s*(?<num>\d+(?:\.\d+)?)\s*[巻話冊]|(?:vol\.?|volume|v)\s*(?<num>\d+(?:\.\d+)?)|(?<=\s|\b)(?<num>\d{1,3}(?:\.\d+)?)\s*$|(?<=[^\d\s])(?<num>0\d|\d{2})(?=\s|$))", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VolumeNumberRegex();

    [GeneratedRegex(@"(?<!\d)(?<isbn>97[89][-\s]?\d{1,5}[-\s]?\d{1,7}[-\s]?\d{1,7}[-\s]?\d|\d{9}[\dXx])(?!\d)", RegexOptions.Compiled)]
    private static partial Regex IsbnCandidateRegex();

    private static readonly string[] KnownImprintKeywords =
    [
        "文庫", "ブックス", "ノベルズ", "ノベルス", "コミックス", "新書", "選書",
        "Bunko", "Books", "Novels", "Comics", "PASH!", "電撃", "角川", "ガガガ", "MF", "GA", "スニーカー", "ファンタジア"
    ];

    public static FilenameParsedHints Parse(string fileNameOrTitle)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrTitle))
        {
            return new FilenameParsedHints("", "", "", "", null, "");
        }

        string raw = Path.GetFileNameWithoutExtension(fileNameOrTitle).Trim();
        raw = NormalizeFullWidthAscii(raw);

        string isbn = ExtractIsbn(raw);
        string authors = "";
        string imprint = "";

        // 1. Leading [Author] pattern: "[かさい まり] こぐまのクーク物語"
        var leadMatch = LeadingBracketAuthorRegex().Match(raw);
        if (leadMatch.Success)
        {
            authors = leadMatch.Groups["author"].Value.Trim();
            raw = leadMatch.Groups["rest"].Value.Trim();
        }

        // 2. Trailing " - Author" pattern: "くま クマ 熊 ベアー (PASH! ブックス) - くまなの"
        var trailMatch = TrailingDashAuthorRegex().Match(raw);
        if (trailMatch.Success)
        {
            string trailingAuthor = trailMatch.Groups["author"].Value.Trim();
            if (string.IsNullOrEmpty(authors))
            {
                authors = trailingAuthor;
            }
            raw = raw[..trailMatch.Index].Trim();
        }

        // 3. Parenthesized Imprint / Label e.g. "(PASH! ブックス)" or "(電撃文庫)"
        var parenMatches = ParenthesizedTokenRegex().Matches(raw);
        for (int i = parenMatches.Count - 1; i >= 0; i--)
        {
            var m = parenMatches[i];
            string inner = m.Groups["paren"].Value.Trim();
            if (IsLikelyImprint(inner))
            {
                if (string.IsNullOrEmpty(imprint))
                {
                    imprint = inner;
                }
                raw = raw.Remove(m.Index, m.Length).Trim();
            }
        }

        // 4. Volume index & Series stem
        double? seriesIndex = null;
        string seriesName = "";
        var volMatch = VolumeNumberRegex().Match(raw);
        if (volMatch.Success &&
            double.TryParse(volMatch.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedVol))
        {
            seriesIndex = parsedVol;
            string prefix = raw[..volMatch.Index].Trim(' ', '　', '-', '―', '—', ':', '：', '第', '巻', '#', '(', ')', '（', '）');
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                seriesName = prefix;
            }
        }

        string cleanTitle = Regex.Replace(raw, @"\s{2,}", " ").Trim();
        return new FilenameParsedHints(cleanTitle, authors, imprint, seriesName, seriesIndex, isbn);
    }

    public static string ExtractIsbn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        string normalized = NormalizeFullWidthAscii(text);
        foreach (Match match in IsbnCandidateRegex().Matches(normalized))
        {
            string digits = Regex.Replace(match.Groups["isbn"].Value, @"[-\s]", "").ToUpperInvariant();
            if (IsValidIsbn13(digits) || IsValidIsbn10(digits))
            {
                return digits;
            }
        }

        return "";
    }

    public static bool IsValidIsbn13(string digits)
    {
        if (digits.Length != 13 || (!digits.StartsWith("978", StringComparison.Ordinal) && !digits.StartsWith("979", StringComparison.Ordinal)))
        {
            return false;
        }

        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            if (!char.IsAsciiDigit(digits[i])) return false;
            int d = digits[i] - '0';
            sum += (i % 2 == 0) ? d : d * 3;
        }

        if (!char.IsAsciiDigit(digits[12])) return false;
        int check = (10 - (sum % 10)) % 10;
        return check == (digits[12] - '0');
    }

    public static bool IsValidIsbn10(string digits)
    {
        if (digits.Length != 10) return false;
        int sum = 0;
        for (int i = 0; i < 9; i++)
        {
            if (!char.IsAsciiDigit(digits[i])) return false;
            sum += (digits[i] - '0') * (10 - i);
        }

        int last = digits[9] == 'X' ? 10 : (char.IsAsciiDigit(digits[9]) ? digits[9] - '0' : -1);
        if (last < 0) return false;
        sum += last;
        return sum % 11 == 0;
    }

    private static bool IsLikelyImprint(string text)
    {
        foreach (string kw in KnownImprintKeywords)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static string NormalizeFullWidthAscii(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c is >= '！' and <= '～')
            {
                sb.Append((char)(c - 0xFEE0));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

