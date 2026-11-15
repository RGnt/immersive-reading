namespace ImmersiveReading.Plugins.DeckBuilder;

// Adapted from jiten.moe's ExampleSentenceExtractor (https://github.com/Sirush/Jiten, Apache-2.0).
// See NOTICE-Jiten.md. Rewritten for this app: words are matched by deck key instead of JMdict word id.

internal sealed record SentenceToken(string Key, int Start, int Length, bool IsContent, string Text);

internal sealed record ParsedSentence(string Text, int Index, IReadOnlyList<SentenceToken> Tokens);

/// <summary>
/// Picks one example sentence per deck word, the way jiten.moe does: only well-formed sentences qualify
/// (at least 6 distinct characters and 2 content words), and three passes widen the allowed length and the
/// part of the book that is searched. Within a pass the longest sentences go first, every sentence is used at
/// most once and gives an example to every word that is still waiting for one.
/// </summary>
internal static class ExampleSentenceSelector
{
    private readonly record struct Pass(int MinLength, int MaxLength, double Fraction);

    private static readonly Pass[] Passes = [new(15, 35, 0.25), new(10, 45, 0.5), new(10, 55, 1.0)];

    private static readonly HashSet<char> ClosingSigns = ['〉', '》', '】', '〕', '）', ')', '」', '』', '｝', '}'];
    private static readonly (char Open, char Close)[] BracketPairs = [('「', '」'), ('『', '』'), ('（', '）'), ('(', ')')];

    /// <summary>Assigns <see cref="DeckWord.ExampleSentence"/> (target marked with <c>**</c>) to the words.</summary>
    public static void Assign(IReadOnlyList<ParsedSentence> sentences, IReadOnlyDictionary<string, DeckWord> wordsByKey)
    {
        var waiting = new HashSet<string>(wordsByKey.Keys);
        var used = new HashSet<int>();
        var valid = sentences.Where(IsValid).ToList();

        foreach (var pass in Passes)
        {
            int maxPosition = (int)(sentences.Count * pass.Fraction);
            var candidates = valid
                .Where(s => !used.Contains(s.Index) && s.Text.Length >= pass.MinLength && s.Text.Length <= pass.MaxLength && s.Index < maxPosition)
                .OrderByDescending(s => s.Text.Length)
                .ThenBy(s => s.Index)
                .ToList();

            foreach (var sentence in candidates)
            {
                var (text, shift) = Tidy(sentence.Text);
                foreach (var token in sentence.Tokens)
                {
                    if (!waiting.Remove(token.Key)) continue;
                    int start = token.Start + shift;
                    if (start < 0 || start + token.Length > text.Length) continue;
                    wordsByKey[token.Key].ExampleSentence =
                        text[..start] + "**" + text.Substring(start, token.Length) + "**" + text[(start + token.Length)..];
                }

                used.Add(sentence.Index);
                if (waiting.Count == 0) return;
            }
        }
    }

    private static bool IsValid(ParsedSentence s)
    {
        var chars = new HashSet<char>();
        int content = 0;
        foreach (var t in s.Tokens)
        {
            foreach (char c in t.Text) chars.Add(c);
            if (t.IsContent) content++;
        }
        return chars.Count >= 6 && content >= 2;
    }

    /// <summary>Drops leading closing brackets, collapses a repeated final mark and balances 「」『』（）.</summary>
    internal static (string Text, int Shift) Tidy(string original)
    {
        string text = NormalizeTrailingPunctuation(original).Trim();
        int shift = -(original.Length - original.TrimStart().Length);

        int closing = 0;
        while (closing < text.Length && ClosingSigns.Contains(text[closing])) closing++;
        if (closing > 0)
        {
            text = text[closing..];
            shift -= closing;
        }

        foreach (var (open, close) in BracketPairs)
        {
            int balance = 0;
            foreach (char c in text)
            {
                if (c == open) balance++;
                else if (c == close) balance--;
            }

            if (balance > 0)
            {
                text += new string(close, balance);
            }
            else if (balance < 0)
            {
                text = new string(open, -balance) + text;
                shift += -balance;
            }
        }

        return (text, shift);
    }

    internal static string NormalizeTrailingPunctuation(string text)
    {
        int end = text.Length;
        while (end >= 2 && text[end - 1] == text[end - 2] && text[end - 1] is '。' or '！' or '？' or '!' or '?' or '.' or '…' or '‥')
            end--;
        return end == text.Length ? text : text[..end];
    }
}
