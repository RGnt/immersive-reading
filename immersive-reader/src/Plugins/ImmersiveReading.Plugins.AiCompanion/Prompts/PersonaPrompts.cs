namespace ImmersiveReading.Plugins.AiCompanion.Prompts;

public static class PersonaPrompts
{
    public static string BuildSystemPrompt(
        string personaName,
        string roleSummary,
        string speechStyle,
        string voiceSamplesBlock,
        int cappedChapterIndex,
        int cappedTokenId) =>
        $$"""
        You are roleplaying as the book character "{{personaName}}" ({{roleSummary}}).
        SPEECH_STYLE: {{speechStyle}}
        VOICE_SAMPLES:
        {{voiceSamplesBlock}}
        SPOILER_BOUNDARY: Chapter {{cappedChapterIndex}}, Token {{cappedTokenId}}. Never mention events beyond this point.
        Respond in JSON: {"reply": "..."}
        """;

    public static string BuildUserPrompt(string storyContextSummary, string targetPlusOneList, string userMessage) =>
        $$"""
        STORY_CONTEXT_UP_TO_PROGRESS:
        {{storyContextSummary}}
        TARGET_PLUS_ONE: {{targetPlusOneList}}
        USER_MESSAGE: {{userMessage}}
        """;

    public static string BuildRewritePrompt(string personaName, string speechStyle, string outOfBudgetList, string targetPlusOneList, string candidateReply) =>
        $$"""
        Your candidate reply used out-of-budget advanced words that the learner does not know yet: [{{outOfBudgetList}}].
        Rewrite the reply in character as "{{personaName}}" ({{speechStyle}}):
        1. Replace [{{outOfBudgetList}}] with simple everyday Known words.
        2. Naturally include at least one "+1" target word from: [{{targetPlusOneList}}].
        TARGET_PLUS_ONE: {{targetPlusOneList}}
        CANDIDATE_REPLY: {{candidateReply}}
        Respond in JSON: {"reply": "..."}
        """;
}

