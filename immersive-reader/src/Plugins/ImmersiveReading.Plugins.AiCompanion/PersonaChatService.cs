using System.Text.Json;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Library;

namespace ImmersiveReading.Plugins.AiCompanion;

public sealed record BookPersonaRecord(
    string PersonaId,
    string? BookId,
    string Name,
    string RoleSummary,
    string SpeechStyle,
    IReadOnlyList<string> VoiceSamples,
    int? MaxSpoilerChapter,
    int? MaxSpoilerToken,
    string CreatedAtUtc
);

public sealed record PersonaChatMessageRecord(
    string MessageId,
    string ChatId,
    string SenderRole,
    string RawText,
    string EnrichedHtml,
    string SyntheticChapterHref,
    IReadOnlyList<string> PlusOneWordsUsed,
    double ComprehensionRatio,
    string CreatedAtUtc
);

public sealed record PersonaTurnResult(
    string ChatId,
    BookPersonaRecord Persona,
    int CappedChapterIndex,
    int CappedTokenId,
    string StoryContextSummary,
    bool TriggeredNPlusOneRewrite,
    IReadOnlyList<string> InitialOutOfBudgetLemmas,
    VocabularyAuditResult FinalVocabularyAudit,
    PersonaChatMessageRecord UserMessage,
    PersonaChatMessageRecord PersonaMessage
);

internal sealed record PersonaReplyDto(
    string Reply
);

public sealed class PersonaChatService
{
    private readonly LibraryDatabase _db;
    private readonly IReaderContextService _readerContext;
    private readonly IVocabularyBudgetService _vocabBudget;
    private readonly ISnippetEnricher _snippetEnricher;
    private readonly ILanguagePlugin _languagePlugin;
    private readonly ILlmProvider _llmProvider;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PersonaChatService(
        LibraryDatabase db,
        IReaderContextService readerContext,
        IVocabularyBudgetService vocabBudget,
        ISnippetEnricher snippetEnricher,
        ILanguagePlugin languagePlugin,
        ILlmProvider llmProvider)
    {
        _db = db;
        _readerContext = readerContext;
        _vocabBudget = vocabBudget;
        _snippetEnricher = snippetEnricher;
        _languagePlugin = languagePlugin;
        _llmProvider = llmProvider;
    }

    public BookPersonaRecord CreatePersona(
        string? bookId,
        string name,
        string roleSummary,
        string speechStyle,
        IReadOnlyList<string>? voiceSamples = null,
        int? maxSpoilerChapter = null,
        int? maxSpoilerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string personaId = $"prs-{Guid.NewGuid():N}"[..20];
        string now = DateTime.UtcNow.ToString("O");
        var samples = voiceSamples ?? [];
        string samplesJson = JsonSerializer.Serialize(samples, JsonOpts);

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO book_personas (
                persona_id, book_id, name, role_summary, speech_style,
                voice_samples_json, max_spoiler_chapter, max_spoiler_token, created_at_utc
            ) VALUES (
                $pid, $bid, $name, $role, $style, $samples, $maxCh, $maxTok, $now
            );
            """;
        cmd.Parameters.AddWithValue("$pid", personaId);
        cmd.Parameters.AddWithValue("$bid", (object?)bookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$name", name.Trim());
        cmd.Parameters.AddWithValue("$role", roleSummary?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$style", speechStyle?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$samples", samplesJson);
        cmd.Parameters.AddWithValue("$maxCh", (object?)maxSpoilerChapter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxTok", (object?)maxSpoilerToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new BookPersonaRecord(
            PersonaId: personaId,
            BookId: bookId,
            Name: name.Trim(),
            RoleSummary: roleSummary?.Trim() ?? "",
            SpeechStyle: speechStyle?.Trim() ?? "",
            VoiceSamples: samples,
            MaxSpoilerChapter: maxSpoilerChapter,
            MaxSpoilerToken: maxSpoilerToken,
            CreatedAtUtc: now);
    }

    public IReadOnlyList<BookPersonaRecord> ListPersonas(string? bookId = null, string? defaultBookTitle = null)
    {
        var list = new List<BookPersonaRecord>();
        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            if (!string.IsNullOrWhiteSpace(bookId))
            {
                cmd.CommandText = """
                    SELECT persona_id, book_id, name, role_summary, speech_style, voice_samples_json, max_spoiler_chapter, max_spoiler_token, created_at_utc
                    FROM book_personas
                    WHERE book_id = $bid OR book_id IS NULL
                    ORDER BY created_at_utc ASC;
                    """;
                cmd.Parameters.AddWithValue("$bid", bookId);
            }
            else
            {
                cmd.CommandText = """
                    SELECT persona_id, book_id, name, role_summary, speech_style, voice_samples_json, max_spoiler_chapter, max_spoiler_token, created_at_utc
                    FROM book_personas
                    ORDER BY created_at_utc ASC;
                    """;
            }

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadPersonaRow(r));
            }
        }

        if (list.Count == 0 && !string.IsNullOrWhiteSpace(bookId))
        {
            string defaultName = (defaultBookTitle?.Contains("クーク", StringComparison.Ordinal) == true)
                ? "クーク"
                : "読書コンパニオン (Book Character)";
            var seeded = CreatePersona(
                bookId: bookId,
                name: defaultName,
                roleSummary: "物語の登場人物・読書パートナー",
                speechStyle: "一人称は「ぼく」。親しみやすく優しい口調（「〜だよ」「〜だね」）。",
                voiceSamples: ["「ぼくにとって、うきわは海の中で、はちみつより、だいじかもしれない」"]);
            list.Add(seeded);
        }

        return list;
    }

    public BookPersonaRecord? GetPersona(string personaId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT persona_id, book_id, name, role_summary, speech_style, voice_samples_json, max_spoiler_chapter, max_spoiler_token, created_at_utc
            FROM book_personas
            WHERE persona_id = $pid;
            """;
        cmd.Parameters.AddWithValue("$pid", personaId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadPersonaRow(r) : null;
    }

    public string StartOrGetChatSession(string personaId, string? bookId = null, bool nPlusOneStrict = true)
    {
        using var conn = _db.OpenConnection();
        using (var findCmd = conn.CreateCommand())
        {
            findCmd.CommandText = "SELECT chat_id FROM persona_chat_sessions WHERE persona_id = $pid ORDER BY created_at_utc DESC LIMIT 1;";
            findCmd.Parameters.AddWithValue("$pid", personaId);
            if (findCmd.ExecuteScalar() is string existingChatId)
            {
                return existingChatId;
            }
        }

        string chatId = $"chat-{Guid.NewGuid():N}"[..20];
        string now = DateTime.UtcNow.ToString("O");
        using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = """
                INSERT INTO persona_chat_sessions (chat_id, persona_id, book_id, n_plus_one_strict, created_at_utc)
                VALUES ($cid, $pid, $bid, $strict, $now);
                """;
            insCmd.Parameters.AddWithValue("$cid", chatId);
            insCmd.Parameters.AddWithValue("$pid", personaId);
            insCmd.Parameters.AddWithValue("$bid", (object?)bookId ?? DBNull.Value);
            insCmd.Parameters.AddWithValue("$strict", nPlusOneStrict ? 1 : 0);
            insCmd.Parameters.AddWithValue("$now", now);
            insCmd.ExecuteNonQuery();
        }
        return chatId;
    }

    public IReadOnlyList<PersonaChatMessageRecord> ListChatMessages(string chatId)
    {
        var list = new List<PersonaChatMessageRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT message_id, chat_id, sender_role, raw_text, enriched_html, plus_one_words_json, COALESCE(comprehension_ratio, 1.0), created_at_utc
            FROM persona_chat_messages
            WHERE chat_id = $cid
            ORDER BY created_at_utc ASC;
            """;
        cmd.Parameters.AddWithValue("$cid", chatId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var plusOne = JsonSerializer.Deserialize<List<string>>(r.GetString(5), JsonOpts) ?? [];
            list.Add(new PersonaChatMessageRecord(
                MessageId: r.GetString(0),
                ChatId: r.GetString(1),
                SenderRole: r.GetString(2),
                RawText: r.GetString(3),
                EnrichedHtml: r.GetString(4),
                SyntheticChapterHref: "",
                PlusOneWordsUsed: plusOne,
                ComprehensionRatio: r.GetDouble(6),
                CreatedAtUtc: r.GetString(7)));
        }
        return list;
    }

    public async Task<PersonaTurnResult> SendMessageAsync(
        string personaId,
        string userMessage,
        IReadOnlyCollection<string>? chapterTargetWords = null,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        int? overrideViewportTokenId = null,
        CancellationToken ct = default)
    {
        var persona = GetPersona(personaId)
            ?? throw new KeyNotFoundException($"Persona '{personaId}' not found.");

        string bookId = persona.BookId ?? "book-global";
        string chatId = StartOrGetChatSession(personaId, persona.BookId, nPlusOneStrict: true);

        // 1. Spoiler Guard: Fetch story context strictly up to the user's current reading position
        int? effectiveCapChapter = overrideChapterIndex ?? persona.MaxSpoilerChapter;
        int? effectiveCapToken = overrideViewportTokenId ?? persona.MaxSpoilerToken;

        var contextSlices = await _readerContext.GetStoryContextUpToProgressAsync(
            bookId: bookId,
            overrideChapterIndex: effectiveCapChapter,
            overrideSpineHref: overrideSpineHref,
            overrideViewportTokenId: effectiveCapToken,
            ct: ct).ConfigureAwait(false);

        var latestSlice = contextSlices.Count > 0
            ? contextSlices[^1]
            : new ReadPassageSlice(bookId, bookId, effectiveCapChapter ?? 0, "", overrideSpineHref ?? "", 0, effectiveCapToken ?? 0, "", []);

        int cappedChapterIndex = latestSlice.ChapterIndex;
        int cappedTokenId = latestSlice.EndTokenId;
        string storyContextSummary = string.Join("\n---\n", contextSlices.Select(s => $"[Ch {s.ChapterIndex} tokens {s.StartTokenId}..{s.EndTokenId}]: {s.PlainText}"));

        // 2. Build n+1 Vocabulary Budget
        var vocabProfile = _vocabBudget.BuildNPlusOneProfile(
            language: "ja",
            bookId: bookId,
            chapterTargetWords: chapterTargetWords,
            maxTurnTargets: 5);

        string voiceSamplesBlock = persona.VoiceSamples.Count > 0
            ? string.Join("\n", persona.VoiceSamples.Select(v => $"- {v}"))
            : "（自然な会話口調）";
        string targetPlusOneList = string.Join(", ", vocabProfile.SelectedTurnTargets);

        string systemPrompt = $$"""
            You are roleplaying as the book character "{{persona.Name}}" ({{persona.RoleSummary}}).
            SPEECH_STYLE: {{persona.SpeechStyle}}
            VOICE_SAMPLES:
            {{voiceSamplesBlock}}
            SPOILER_BOUNDARY: Chapter {{cappedChapterIndex}}, Token {{cappedTokenId}}. Never mention events beyond this point.
            Respond in JSON: {"reply": "..."}
            """;

        string userPrompt = $$"""
            STORY_CONTEXT_UP_TO_PROGRESS:
            {{storyContextSummary}}
            TARGET_PLUS_ONE: {{targetPlusOneList}}
            USER_MESSAGE: {{userMessage}}
            """;

        // 3. Initial Candidate Reply from LLM
        var initialResp = await _llmProvider.CompleteStructuredAsync<PersonaReplyDto>(
            new LlmRequest(
                Messages:
                [
                    new LlmMessage("system", systemPrompt),
                    new LlmMessage("user", userPrompt)
                ],
                Temperature: 0.4,
                TaskKind: "persona.chat.initial"),
            ct).ConfigureAwait(false);

        string candidateReply = initialResp.Value.Reply ?? "";

        // 4. Closed-Loop MeCab.DotNet n+1 Verification
        using var tokenizer = _languagePlugin.CreateTokenizer();
        var initialTokens = tokenizer.TokenizeParagraph(candidateReply, []);
        var initialAudit = _vocabBudget.AuditReplyTokens(initialTokens, vocabProfile);

        bool triggeredRewrite = false;
        var finalAudit = initialAudit;
        string finalReply = candidateReply;

        if (initialAudit.OutOfBudgetLemmas.Count > 0)
        {
            triggeredRewrite = true;
            string outOfBudgetList = string.Join(", ", initialAudit.OutOfBudgetLemmas);
            string rewritePrompt = $$"""
                Your candidate reply used out-of-budget advanced words that the learner does not know yet: [{{outOfBudgetList}}].
                Rewrite the reply in character as "{{persona.Name}}" ({{persona.SpeechStyle}}):
                1. Replace [{{outOfBudgetList}}] with simple everyday Known words.
                2. Naturally include at least one "+1" target word from: [{{targetPlusOneList}}].
                TARGET_PLUS_ONE: {{targetPlusOneList}}
                CANDIDATE_REPLY: {{candidateReply}}
                Respond in JSON: {"reply": "..."}
                """;

            var rewriteResp = await _llmProvider.CompleteStructuredAsync<PersonaReplyDto>(
                new LlmRequest(
                    Messages:
                    [
                        new LlmMessage("system", systemPrompt),
                        new LlmMessage("user", rewritePrompt)
                    ],
                    Temperature: 0.2,
                    TaskKind: "persona.chat.rewrite"),
                ct).ConfigureAwait(false);

            finalReply = rewriteResp.Value.Reply ?? candidateReply;
            var rewrittenTokens = tokenizer.TokenizeParagraph(finalReply, []);
            finalAudit = _vocabBudget.AuditReplyTokens(rewrittenTokens, vocabProfile);
        }

        // 5. Enrich both user and persona messages via ISnippetEnricher (AngleSharpDomEnricher)
        var enrichedUser = _snippetEnricher.EnrichSnippet(bookId, userMessage, "ja", vocabProfile.TargetPlusOneLemmas);
        var enrichedPersona = _snippetEnricher.EnrichSnippet(bookId, finalReply, "ja", vocabProfile.TargetPlusOneLemmas);

        var userRecord = SaveChatMessage(chatId, "user", userMessage, enrichedUser.EnrichedHtml, enrichedUser.SyntheticChapterHref, [], 1.0);
        var personaRecord = SaveChatMessage(
            chatId,
            "persona",
            finalReply,
            enrichedPersona.EnrichedHtml,
            enrichedPersona.SyntheticChapterHref,
            finalAudit.UsedPlusOneLemmas,
            finalAudit.KnownRatio);

        return new PersonaTurnResult(
            ChatId: chatId,
            Persona: persona,
            CappedChapterIndex: cappedChapterIndex,
            CappedTokenId: cappedTokenId,
            StoryContextSummary: storyContextSummary,
            TriggeredNPlusOneRewrite: triggeredRewrite,
            InitialOutOfBudgetLemmas: initialAudit.OutOfBudgetLemmas,
            FinalVocabularyAudit: finalAudit,
            UserMessage: userRecord,
            PersonaMessage: personaRecord);
    }

    private PersonaChatMessageRecord SaveChatMessage(
        string chatId,
        string role,
        string rawText,
        string enrichedHtml,
        string syntheticHref,
        IReadOnlyList<string> plusOneWords,
        double ratio)
    {
        string msgId = $"msg-{Guid.NewGuid():N}"[..20];
        string now = DateTime.UtcNow.ToString("O");
        string plusOneJson = JsonSerializer.Serialize(plusOneWords, JsonOpts);

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO persona_chat_messages (
                message_id, chat_id, sender_role, raw_text, enriched_html, plus_one_words_json, comprehension_ratio, created_at_utc
            ) VALUES (
                $mid, $cid, $role, $raw, $html, $plus, $ratio, $now
            );
            """;
        cmd.Parameters.AddWithValue("$mid", msgId);
        cmd.Parameters.AddWithValue("$cid", chatId);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$raw", rawText);
        cmd.Parameters.AddWithValue("$html", enrichedHtml);
        cmd.Parameters.AddWithValue("$plus", plusOneJson);
        cmd.Parameters.AddWithValue("$ratio", ratio);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new PersonaChatMessageRecord(
            MessageId: msgId,
            ChatId: chatId,
            SenderRole: role,
            RawText: rawText,
            EnrichedHtml: enrichedHtml,
            SyntheticChapterHref: syntheticHref,
            PlusOneWordsUsed: plusOneWords,
            ComprehensionRatio: ratio,
            CreatedAtUtc: now);
    }

    private static BookPersonaRecord ReadPersonaRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        var samples = JsonSerializer.Deserialize<List<string>>(r.GetString(5), JsonOpts) ?? [];
        return new BookPersonaRecord(
            PersonaId: r.GetString(0),
            BookId: r.IsDBNull(1) ? null : r.GetString(1),
            Name: r.GetString(2),
            RoleSummary: r.GetString(3),
            SpeechStyle: r.GetString(4),
            VoiceSamples: samples,
            MaxSpoilerChapter: r.IsDBNull(6) ? null : r.GetInt32(6),
            MaxSpoilerToken: r.IsDBNull(7) ? null : r.GetInt32(7),
            CreatedAtUtc: r.GetString(8));
    }
}
