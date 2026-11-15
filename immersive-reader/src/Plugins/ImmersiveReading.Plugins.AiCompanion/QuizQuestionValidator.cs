using ImmersiveReading.Core.Knowledge;

namespace ImmersiveReading.Plugins.AiCompanion;

/// <summary>A question that passed every check. For multiple choice the options are already shuffled and <see cref="CorrectIndex"/> points at the right one.</summary>
internal sealed record ValidatedQuestion(
    string Prompt, IReadOnlyList<string> Options, int CorrectIndex, string EvidenceQuote, int ParagraphId, string Explanation, string ModelAnswer);

/// <summary>
/// Nothing the model says about a question is trusted. The evidence must be a real sentence of the passage, a multiple-choice question must have
/// exactly four distinct options with exactly one correct one and no tell (a correct answer that is much longer than the others, or that is the
/// evidence sentence copied), and the position of the correct answer is decided here, not by the model (models favour the first option).
/// A rejected question is asked for again once with the reason; there is no canned fallback.
/// </summary>
internal static class QuizQuestionValidator
{
    public const int OptionCount = 4;
    private const int MinEvidenceChars = 6;

    private static readonly string[] BannedOptions = ["all of the above", "none of the above", "上のすべて", "上記のすべて", "どれでもない", "すべて正しい", "全部正しい", "いずれでもない"];

    public static (ValidatedQuestion? Question, string? Problem) Validate(GeneratedQuestionDto dto, QuizPassage passage, bool multipleChoice, Random rng)
    {
        string prompt = (dto.QuestionPrompt ?? "").Trim();
        if (prompt.Length == 0) return (null, "the question is empty");
        if (prompt.Length > 300) return (null, "the question is longer than 300 characters");

        string evidence = (dto.EvidenceQuote ?? "").Trim();
        string evidenceKey = KnowledgeMatch.Normalize(evidence);
        if (evidenceKey.Length < MinEvidenceChars) return (null, "evidenceQuote is missing or too short");
        if (!KnowledgeMatch.Normalize(passage.PlainText).Contains(evidenceKey, StringComparison.Ordinal))
            return (null, $"evidenceQuote was not found in the passage (you wrote: 「{(evidence.Length > 40 ? evidence[..40] + "…" : evidence)}」)");

        // the paragraph that really contains the evidence, whatever id the model gave
        var home = passage.Paragraphs.FirstOrDefault(p => KnowledgeMatch.Normalize(p.Text).Contains(evidenceKey, StringComparison.Ordinal))
                   ?? passage.Paragraphs[0];

        string explanation = (dto.Explanation ?? "").Trim();
        string modelAnswer = (dto.ModelAnswer ?? "").Trim();
        if (modelAnswer.Length == 0) modelAnswer = evidence;

        if (!multipleChoice)
            return (new ValidatedQuestion(prompt, [], 0, evidence, home.ParagraphId, explanation, modelAnswer), null);

        var options = (dto.Options ?? []).Select(o => (o ?? "").Trim()).ToList();
        if (options.Count != OptionCount) return (null, $"a multiple-choice question needs exactly {OptionCount} options (got {options.Count})");
        if (options.Any(o => o.Length == 0 || o.Length > 160)) return (null, "every option must be 1 to 160 characters");
        if (dto.CorrectOptionIndex < 0 || dto.CorrectOptionIndex >= OptionCount) return (null, "correctOptionIndex must be 0, 1, 2 or 3");

        var keys = options.Select(KnowledgeMatch.Normalize).ToList();
        if (keys.Any(k => k.Length == 0)) return (null, "an option has no text");
        if (keys.Distinct(StringComparer.Ordinal).Count() != OptionCount) return (null, "two options say the same thing: exactly one option may be correct");
        if (keys.Contains(KnowledgeMatch.Normalize(prompt), StringComparer.Ordinal)) return (null, "an option repeats the question");
        if (options.Any(o => BannedOptions.Any(b => o.Contains(b, StringComparison.OrdinalIgnoreCase))))
            return (null, "options such as 'all of the above' or 'none of the above' are not allowed");

        int correct = dto.CorrectOptionIndex;
        var wrong = Enumerable.Range(0, OptionCount).Where(i => i != correct).Select(i => options[i].Length).ToList();
        int longestWrong = wrong.Max(), shortestWrong = wrong.Min();
        if (options[correct].Length > 1.8 * longestWrong && options[correct].Length - longestWrong > 12)
            return (null, "the correct option is much longer than the wrong ones, which gives it away: make the options similar in length and detail");
        if (options[correct].Length < 0.4 * shortestWrong && shortestWrong - options[correct].Length > 12)
            return (null, "the correct option is much shorter than the wrong ones: make the options similar in length and detail");
        string correctKey = keys[correct];
        if (correctKey.Length >= 0.8 * evidenceKey.Length && (evidenceKey.Contains(correctKey, StringComparison.Ordinal) || correctKey.Contains(evidenceKey, StringComparison.Ordinal)))
            return (null, "the correct option just copies the evidence sentence: put it in other words, like the wrong options");

        // the position of the right answer is random
        var order = Enumerable.Range(0, OptionCount).ToList();
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        var shuffled = order.Select(i => options[i]).ToList();
        int newCorrect = order.IndexOf(correct);
        return (new ValidatedQuestion(prompt, shuffled, newCorrect, evidence, home.ParagraphId, explanation, modelAnswer), null);
    }
}
