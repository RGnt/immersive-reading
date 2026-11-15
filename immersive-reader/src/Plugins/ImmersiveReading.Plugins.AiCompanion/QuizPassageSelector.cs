using System.Text;
using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.Plugins.AiCompanion;

/// <param name="PlainText">The passage as plain paragraphs: what the answer must be found in (quotes are verified against it).</param>
/// <param name="PromptText">The same paragraphs, each numbered (<c>[12] text</c>), as the model sees them.</param>
public sealed record QuizPassage(IReadOnlyList<ParagraphSlice> Paragraphs, string PlainText, string PromptText, int StartTokenId, int EndTokenId);

/// <summary>
/// Picks the stretch of the last completed chapter a question is asked about: a run of whole paragraphs that fits the model's budget. A short
/// chapter is used whole; a long one gets a random window, avoiding the windows used by earlier quizzes of the same chapter while the chapter
/// has room for it, so repeated quizzes cover different parts of it.
/// </summary>
public static class QuizPassageSelector
{
    public const int DefaultMaxChars = 2800;

    public static QuizPassage Select(IReadOnlyList<ParagraphSlice> paragraphs, int maxChars, Random rng, IReadOnlyList<(int Start, int End)>? recent = null)
    {
        var usable = paragraphs.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => p with { Text = p.Text.Trim() }).ToList();
        if (usable.Count == 0) throw new InvalidOperationException("The chapter has no text to ask about.");
        maxChars = Math.Max(200, maxChars);

        int total = usable.Sum(p => p.Text.Length);
        if (total <= maxChars) return Build(usable);

        // every start paragraph with the longest run of whole paragraphs that fits (a paragraph longer than the budget is cut at a sentence end)
        var windows = new List<List<ParagraphSlice>>();
        for (int start = 0; start < usable.Count; start++)
        {
            var run = new List<ParagraphSlice>();
            int chars = 0;
            for (int i = start; i < usable.Count; i++)
            {
                var p = usable[i];
                if (run.Count > 0 && chars + p.Text.Length > maxChars) break;
                run.Add(p.Text.Length > maxChars ? p with { Text = CutAtSentence(p.Text, maxChars) } : p);
                chars += run[^1].Text.Length;
            }
            // a window that is mostly empty (the tail of the chapter) is not a good question source
            if (chars >= Math.Min(maxChars / 2, total / 2)) windows.Add(run);
        }
        if (windows.Count == 0) windows.Add(usable.Take(1).ToList());

        var fresh = recent is { Count: > 0 }
            ? windows.Where(w => !recent.Any(r => w[0].StartTokenId <= r.End && w[^1].EndTokenId >= r.Start)).ToList()
            : windows;
        var pool = fresh.Count > 0 ? fresh : windows;
        return Build(pool[rng.Next(pool.Count)]);
    }

    private static QuizPassage Build(IReadOnlyList<ParagraphSlice> paragraphs)
    {
        var plain = new StringBuilder();
        var prompt = new StringBuilder();
        foreach (var p in paragraphs)
        {
            if (plain.Length > 0) { plain.Append('\n'); prompt.Append('\n'); }
            plain.Append(p.Text);
            prompt.Append('[').Append(p.ParagraphId).Append("] ").Append(p.Text);
        }
        return new QuizPassage(paragraphs, plain.ToString(), prompt.ToString(), paragraphs[0].StartTokenId, paragraphs[^1].EndTokenId);
    }

    private static string CutAtSentence(string text, int max)
    {
        int cut = text.LastIndexOfAny(['。', '！', '？', '!', '?', '」'], Math.Min(max, text.Length) - 1);
        return cut > max / 3 ? text[..(cut + 1)] : text[..max];
    }
}
