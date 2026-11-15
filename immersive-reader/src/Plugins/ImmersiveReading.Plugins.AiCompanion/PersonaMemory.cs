using System.Diagnostics;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Knowledge;
using ImmersiveReading.Core.Prompts;
using ImmersiveReading.Plugins.AiCompanion.Prompts;

namespace ImmersiveReading.Plugins.AiCompanion;

public sealed class MemorySettings
{
    /// <summary>Characters recall what the user told them and remember new facts from the conversation.</summary>
    public bool CharacterMemory { get; set; } = true;
    /// <summary><c>all</c>: every character may use a fact; <c>thisCharacter</c>: only the one the user told.</summary>
    public string CharacterMemoryShare { get; set; } = "all";
    public int MemoryMaxFacts { get; set; } = 3;
    /// <summary>Free-form quiz answers move the status of the words the learner used (one correct use never promotes a word; see the quiz rubric).</summary>
    public bool QuizNudgesVocabulary { get; set; } = true;
}

public sealed class MemoryFactDto
{
    public string? Text { get; set; }
    public string? Subject { get; set; }
    public string? Quote { get; set; }
}

public sealed class MemoryExtractionDto
{
    public List<MemoryFactDto>? Facts { get; set; }
}

/// <summary>
/// What the characters know beyond the book (spec 24, phase 2): before a reply, the facts the user told earlier (and story facts that fit the
/// message) are recalled through the knowledge hub; after a reply, facts the user stated in their own message are remembered. Memory only
/// enriches a conversation: any failure here is logged and the chat goes on as if there were no memory.
/// </summary>
public sealed class PersonaMemory(IKnowledgeHub hub, IPromptCatalog prompts, ILlmProvider llm, Func<MemorySettings> settings)
{
    private const int FactsInBlock = 8;

    public static string Audience(string personaId) => $"persona:{personaId}";

    // ------------------------------------------------------------------ recall
    /// <summary>The text to append to the character's system prompt, or "" when there is nothing to add (or memory is off).</summary>
    public async Task<string> BuildBlockAsync(BookPersonaRecord persona, string userMessage, int chapterIndex, CancellationToken ct)
    {
        if (!settings().CharacterMemory) return "";
        try
        {
            var facts = await RecallFactsAsync(persona, userMessage, FactsInBlock, ct).ConfigureAwait(false);
            IReadOnlyList<KnowledgeEntry> story = persona.BookId is null || persona.BookId == "book-global"
                ? []
                : await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book(persona.BookId), userMessage, ["event", "item"],
                    KnowledgeViewpoint.AtChapter(chapterIndex), Limit: 4), ct).ConfigureAwait(false);
            if (facts.Count == 0 && story.Count == 0) return "";
            return prompts.Render(MemoryPrompts.BlockName, new Dictionary<string, object?>
            {
                ["user_facts"] = facts.Select(f => f.Text).ToList(),
                ["story_facts"] = story.Select(e => e.Text).ToList()
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"Character memory could not be recalled: {ex.Message}");
            return "";
        }
    }

    /// <summary>The facts this character may use that fit the message, then the newest ones (a fact worded differently from the message must not be missed).</summary>
    private async Task<List<KnowledgeEntry>> RecallFactsAsync(BookPersonaRecord persona, string userMessage, int limit, CancellationToken ct)
    {
        string audience = Audience(persona.PersonaId);
        var matched = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, userMessage, ["fact"], Audience: audience, Limit: limit), ct).ConfigureAwait(false);
        var recent = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, null, ["fact"], Audience: audience, Limit: limit), ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return matched.Concat(recent).Where(f => seen.Add(f.Id)).Take(limit).ToList();
    }

    // ------------------------------------------------------------------ remember
    /// <summary>
    /// Asks the model for facts the user stated in <paramref name="userMessage"/> and stores those it can prove: each fact must come with a quote
    /// that occurs in the user's own message (the model's own words and the book are never stored as facts about the user).
    /// Returns how many facts were stored. Never throws.
    /// </summary>
    public async Task<int> RememberFromMessageAsync(BookPersonaRecord persona, string chatId, string userMessage, CancellationToken ct = default)
    {
        var s = settings();
        if (!s.CharacterMemory) return 0;
        string message = userMessage.Trim();
        if (KnowledgeMatch.Normalize(message).Length < 4) return 0;                    // too short to state a fact
        try
        {
            string audience = Audience(persona.PersonaId);
            var known = await RecallFactsAsync(persona, message, 12, ct).ConfigureAwait(false);
            int max = Math.Clamp(s.MemoryMaxFacts, 1, 5);
            string system = prompts.Render(MemoryPrompts.ExtractName, new Dictionary<string, object?>
            {
                ["user_message"] = message,
                ["known_facts"] = known.Select(k => k.Text).ToList(),
                ["max_facts"] = max
            });
            var request = LlmRequestBuilder.Create()
                .WithSystemPrompt(system)
                .WithUserPrompt("Return the JSON object now.")
                .WithTemperature(0.1)
                .WithTaskKind("memory.extract")
                .Build();
            var response = await llm.CompleteStructuredAsync<MemoryExtractionDto>(request, ct).ConfigureAwait(false);

            string normalizedMessage = KnowledgeMatch.Normalize(message);
            string[]? sharedWith = string.Equals(s.CharacterMemoryShare, "thisCharacter", StringComparison.OrdinalIgnoreCase) ? [audience] : null;
            int stored = 0;
            foreach (var fact in (response.Value.Facts ?? []).Take(max))
            {
                string quote = KnowledgeMatch.Normalize(fact.Quote ?? "");
                if (string.IsNullOrWhiteSpace(fact.Text) || quote.Length < 2 || !normalizedMessage.Contains(quote, StringComparison.Ordinal)) continue;
                try
                {
                    await hub.RememberAsync(new FactInput(KnowledgeScope.User, fact.Text, fact.Subject, $"chat:{chatId}", sharedWith), ct).ConfigureAwait(false);
                    stored++;
                }
                catch (KnowledgeException ex)
                {
                    Trace.TraceWarning($"A fact was not stored: {ex.Message}");
                }
            }
            return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning($"Character memory could not learn from the message: {ex.Message}");
            return 0;
        }
    }
}
