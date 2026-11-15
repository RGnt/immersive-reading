using System.Text;

namespace ImmersiveReading.Plugins.DeckBuilder;

// Deck building is adapted from the way jiten.moe builds decks (https://github.com/Sirush/Jiten, Apache-2.0).
// See NOTICE-Jiten.md.

/// <summary>
/// Splits a paragraph into sentences. A sentence ends at <c>。！？…</c> (plus any closing brackets that follow),
/// but never inside an open 「…」 / 『…』 / （…） pair: a quoted line stays whole and ends at its closing bracket.
/// </summary>
public static class SentenceSplitter
{
    private static readonly HashSet<char> Enders = ['。', '！', '？', '!', '?', '…', '‥'];
    private static readonly HashSet<char> Openers = ['「', '『', '（', '(', '【', '〈', '《', '〔', '｢'];
    private static readonly HashSet<char> Closers = ['」', '』', '）', ')', '】', '〉', '》', '〕', '｣'];

    public static IEnumerable<string> Split(string paragraph)
    {
        var sb = new StringBuilder();
        int depth = 0;
        bool afterEnder = false;

        for (int i = 0; i < paragraph.Length; i++)
        {
            char c = paragraph[i];
            sb.Append(c);

            if (Openers.Contains(c)) depth++;
            else if (Closers.Contains(c)) depth = Math.Max(0, depth - 1);

            if (Enders.Contains(c)) afterEnder = true;
            else if (!Closers.Contains(c)) afterEnder = false;

            if (depth == 0 && afterEnder && (Enders.Contains(c) || Closers.Contains(c)))
            {
                // Swallow the rest of a run like "？！」" so it stays attached to this sentence.
                while (i + 1 < paragraph.Length && (Enders.Contains(paragraph[i + 1]) || Closers.Contains(paragraph[i + 1])))
                {
                    char next = paragraph[++i];
                    sb.Append(next);
                    if (Closers.Contains(next)) depth = Math.Max(0, depth - 1);
                }

                string done = sb.ToString().Trim();
                sb.Clear();
                afterEnder = false;
                if (HasLetters(done)) yield return done;
            }
        }

        string rest = sb.ToString().Trim();
        if (HasLetters(rest)) yield return rest;
    }

    /// <summary>True when the text contains at least one kana, kanji or Latin letter/digit.</summary>
    public static bool HasLetters(string text)
    {
        foreach (char c in text)
        {
            if (IsKana(c) || IsKanji(c) || char.IsLetterOrDigit(c)) return true;
        }
        return false;
    }

    public static bool IsKana(char c) => (c >= 'ぁ' && c <= 'ゟ') || (c >= 'ァ' && c <= 'ヿ' && c != '・');
    public static bool IsKanji(char c) => (c >= '一' && c <= '鿿') || (c >= '㐀' && c <= '䶿') || c == '々';
}
