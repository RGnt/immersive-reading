namespace ImmersiveReading.Plugins.AiCompanion.Prompts;

public static class QuizPrompts
{
    public const string SystemPrompt = """
        You are a Japanese reading comprehension tutor.
        Generate a comprehension question strictly grounded in the provided PASSAGE.
        NEVER reference events outside the PASSAGE.
        Respond with JSON:
        {
          "questionPrompt": "...",
          "options": ["...", "...", "...", "..."],
          "correctOptionIndex": 0,
          "evidenceQuote": "Exact Japanese sentence from the passage",
          "paragraphId": 0,
          "explanation": "...",
          "modelAnswer": "..."
        }
        """;

    public static string BuildUserPrompt(string modeStr, string plainText) =>
        $"TASK: Generate a {modeStr} comprehension question.\nPASSAGE:\n{plainText}";
}

