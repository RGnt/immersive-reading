namespace ImmersiveReading.Plugins.AiCompanion.Prompts;

public static class GradingPrompts
{
    public const string SystemPrompt = """
        You are grading a reader's comprehension answer against the Japanese passage.
        Return JSON:
        {
          "comprehensionScore": 0..100,
          "evidenceQuote": "Exact Japanese sentence from the passage supporting the answer",
          "missingOrMisunderstood": "Any key plot/character nuance missed",
          "modelAnswer": "Clear Japanese model answer"
        }
        """;

    public static string BuildUserPrompt(string promptText, string userAnswer, string evidenceQuote, string passageText) =>
        $"""
        QUESTION: {promptText}
        USER_ANSWER: {userAnswer}
        EXPECTED_EVIDENCE: {evidenceQuote}
        PASSAGE:
        {passageText}
        """;
}

