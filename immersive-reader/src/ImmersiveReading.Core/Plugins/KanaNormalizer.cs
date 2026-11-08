using System.Text;

namespace ImmersiveReading.Core.Plugins;

public static class KanaNormalizer
{
    public static string ToHiragana(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            // Full-width Katakana (\u30A1 .. \u30F6) -> Hiragana (\u3041 .. \u3096)
            if (c is >= '\u30A1' and <= '\u30F6')
            {
                sb.Append((char)(c - 0x60));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static string ToKatakana(ReadOnlySpan<char> input)
    {
        if (input.IsEmpty) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c is >= '\u3041' and <= '\u3096')
            {
                sb.Append((char)(c + 0x60));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static bool IsKanji(Rune r)
    {
        int v = r.Value;
        return v is (>= 0x4E00 and <= 0x9FFF)      // CJK Unified Ideographs
                 or (>= 0x3400 and <= 0x4DBF)      // CJK Extension A
                 or (>= 0x20000 and <= 0x2A6DF)    // CJK Extension B
                 or (>= 0x2A700 and <= 0x2B73F)    // CJK Extension C
                 or (>= 0x2B740 and <= 0x2B81F)    // CJK Extension D
                 or (>= 0x2B820 and <= 0x2CEAF)    // CJK Extension E
                 or (>= 0x2CEB0 and <= 0x2EBEF)    // CJK Extension F
                 or (>= 0x30000 and <= 0x3134F)    // CJK Extension G
                 or (>= 0xF900 and <= 0xFAFF)      // CJK Compatibility Ideographs
                 or (>= 0x2F800 and <= 0x2FA1F)    // CJK Compatibility Ideographs Supplement
                 or 0x3005                         // Ideographic iteration mark '々'
                 or 0x3006                         // Closing mark '〆'
                 or 0x3007;                        // Ideographic number zero '〇'
    }

    public static bool ContainsKanji(ReadOnlySpan<char> input)
    {
        foreach (var r in input.EnumerateRunes())
        {
            if (IsKanji(r)) return true;
        }
        return false;
    }
}

