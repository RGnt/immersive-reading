using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Knowledge;

namespace ImmersiveReading.Plugins.AiCompanion;

// ---- what the model returns (spec: grading is done in this order, each part judged separately) ----
internal sealed class RubricComprehensionDto { public int Score { get; set; } public string? EvidenceQuote { get; set; } public string? Missing { get; set; } }
internal sealed class RubricGrammarIssueDto { public string? Original { get; set; } public string? Correction { get; set; } public string? Note { get; set; } }
internal sealed class RubricGrammarDto { public int Score { get; set; } public List<RubricGrammarIssueDto>? Issues { get; set; } }
internal sealed class RubricWordDto { public string? Surface { get; set; } public string? Lemma { get; set; } public string? Suggestion { get; set; } public string? Note { get; set; } }
internal sealed class RubricVocabularyDto { public int Score { get; set; } public List<RubricWordDto>? WellUsed { get; set; } public List<RubricWordDto>? Misused { get; set; } }

internal sealed class FreeFormRubricDto
{
    public RubricComprehensionDto? Comprehension { get; set; }
    public RubricGrammarDto? Grammar { get; set; }
    public RubricVocabularyDto? Vocabulary { get; set; }
    public string? ModelAnswer { get; set; }
}

// ---- what the app keeps and shows ----
public sealed record RubricIssue(string Original, string Correction, string Note);
public sealed record RubricWord(string Surface, string Lemma, string Suggestion, string Note);

/// <param name="Counted">False when the answer did not answer the question: grammar and vocabulary are then shown but neither scored nor used to move the vocabulary state.</param>
public sealed record FreeFormRubricView(
    int Comprehension, string EvidenceQuote, string Missing,
    bool Counted,
    int? Grammar, IReadOnlyList<RubricIssue> GrammarIssues,
    int? Vocabulary, IReadOnlyList<RubricWord> WellUsed, IReadOnlyList<RubricWord> Misused,
    int Overall);

/// <summary>
/// The free-form rubric, in the order the learner is judged: first whether the answer answers the question (comprehension), then grammar, lastly
/// vocabulary. Grammar and vocabulary only count once the answer addresses the question: a fluent answer to a different question earns no
/// praise and moves no word. The comprehension score alone drives the quiz stage; the overall score (60 / 25 / 15) is for display.
/// Everything the model quotes from the answer is checked against the answer: an issue or a word that is not in what the learner wrote is dropped.
/// </summary>
internal static class FreeFormRubricScorer
{
    /// <summary>Below this comprehension score the answer is treated as not having answered the question.</summary>
    public const int AnsweredThreshold = 40;
    public const int CorrectThreshold = 70;
    public const double ComprehensionWeight = 0.60, GrammarWeight = 0.25, VocabularyWeight = 0.15;
    private const int MaxItems = 5;

    public static FreeFormRubricView Score(FreeFormRubricDto dto, string userAnswer, string fallbackEvidence)
    {
        string answerKey = KnowledgeMatch.Normalize(userAnswer);
        int comprehension = Math.Clamp(dto.Comprehension?.Score ?? 0, 0, 100);
        bool counted = comprehension >= AnsweredThreshold;

        bool InAnswer(string? text)
        {
            string key = KnowledgeMatch.Normalize(text ?? "");
            return key.Length > 0 && answerKey.Contains(key, StringComparison.Ordinal);
        }

        var issues = (dto.Grammar?.Issues ?? [])
            .Where(i => InAnswer(i.Original) && !string.IsNullOrWhiteSpace(i.Correction))
            .Take(MaxItems)
            .Select(i => new RubricIssue(i.Original!.Trim(), i.Correction!.Trim(), (i.Note ?? "").Trim())).ToList();
        var wellUsed = Words(dto.Vocabulary?.WellUsed, InAnswer);
        var misused = Words(dto.Vocabulary?.Misused, InAnswer);
        // a word cannot be both: the misuse wins
        wellUsed = wellUsed.Where(w => misused.All(m => !string.Equals(m.Lemma, w.Lemma, StringComparison.Ordinal))).ToList();

        int? grammar = counted ? Math.Clamp(dto.Grammar?.Score ?? 0, 0, 100) : null;
        int? vocabulary = counted ? Math.Clamp(dto.Vocabulary?.Score ?? 0, 0, 100) : null;
        int overall = counted
            ? (int)Math.Round(ComprehensionWeight * comprehension + GrammarWeight * grammar!.Value + VocabularyWeight * vocabulary!.Value)
            : comprehension;

        return new FreeFormRubricView(
            comprehension, string.IsNullOrWhiteSpace(dto.Comprehension?.EvidenceQuote) ? fallbackEvidence : dto.Comprehension!.EvidenceQuote!.Trim(),
            (dto.Comprehension?.Missing ?? "").Trim(), counted, grammar, issues, vocabulary, wellUsed, misused, overall);
    }

    private static List<RubricWord> Words(List<RubricWordDto>? words, Func<string?, bool> inAnswer)
        => (words ?? [])
            .Where(w => inAnswer(w.Surface))
            .Select(w => new RubricWord(w.Surface!.Trim(), string.IsNullOrWhiteSpace(w.Lemma) ? w.Surface!.Trim() : w.Lemma.Trim(), (w.Suggestion ?? "").Trim(), (w.Note ?? "").Trim()))
            .GroupBy(w => w.Lemma, StringComparer.Ordinal).Select(g => g.First())
            .Take(MaxItems).ToList();

    /// <summary>The evidence the learner's vocabulary gives, for the vocabulary state: only when the answer addressed the question.</summary>
    public static IReadOnlyList<VocabularyPractice> Practice(FreeFormRubricView rubric)
        => !rubric.Counted ? [] : rubric.Misused.Select(w => new VocabularyPractice(w.Lemma, false))
            .Concat(rubric.WellUsed.Select(w => new VocabularyPractice(w.Lemma, true))).ToList();
}
