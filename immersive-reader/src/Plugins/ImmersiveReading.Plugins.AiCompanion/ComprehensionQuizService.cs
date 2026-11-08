using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using Microsoft.Data.Sqlite;
using ImmersiveReading.Plugins.AiCompanion.Prompts;

namespace ImmersiveReading.Plugins.AiCompanion;

public enum ComprehensionStage
{
    MultipleChoice = 0,
    GuidedShortAnswer = 1,
    FreeForm = 2
}

public sealed record EnrichedQuizOption(
    int OptionIndex,
    string Text,
    string EnrichedHtml,
    string SyntheticChapterHref
);

public sealed record ComprehensionQuestionView(
    string QuestionId,
    string SessionId,
    string BookId,
    ComprehensionStage Stage,
    string QuestionMode,
    string PromptText,
    string PromptHtml,
    string PromptSyntheticChapterHref,
    IReadOnlyList<EnrichedQuizOption> Options,
    int ChapterIndex,
    int StartTokenId,
    int EndTokenId,
    int SourceParagraphId,
    string EvidenceQuote
);

public sealed record ComprehensionEvaluationResult(
    string QuestionId,
    string QuestionMode,
    bool IsCorrect,
    int ComprehensionScore,
    string EvidenceQuote,
    int ParagraphId,
    string JumpToParagraphLabel,
    string MissingOrMisunderstood,
    string ModelAnswerHtml,
    string ModelAnswerSyntheticChapterHref,
    ComprehensionStage NextRecommendedStage
);

internal sealed record GeneratedQuestionDto(
    string QuestionPrompt,
    IReadOnlyList<string>? Options,
    int CorrectOptionIndex,
    string EvidenceQuote,
    int ParagraphId,
    string? Explanation = null,
    string? ModelAnswer = null
);

internal sealed record GradedFreeFormDto(
    int ComprehensionScore,
    string EvidenceQuote,
    string MissingOrMisunderstood,
    string ModelAnswer
);

public sealed class ComprehensionQuizService
{
    private readonly IDbConnectionFactory _db;
    private readonly IReaderContextService _readerContext;
    private readonly ISnippetEnricher _snippetEnricher;
    private readonly ILlmProvider _llmProvider;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public ComprehensionQuizService(
        IDbConnectionFactory db,
        IReaderContextService readerContext,
        ISnippetEnricher snippetEnricher,
        ILlmProvider llmProvider)
    {
        _db = db;
        _readerContext = readerContext;
        _snippetEnricher = snippetEnricher;
        _llmProvider = llmProvider;
    }

    /// <summary>
    /// Computes the user's current ComprehensionStage from the last 5 answered questions for this book:
    /// - &lt; 5 answered questions or average score &lt; 0.75 -> MultipleChoice
    /// - average score &gt;= 0.75 and &lt; 0.80 -> GuidedShortAnswer
    /// - average score &gt;= 0.80 -> FreeForm
    /// </summary>
    public ComprehensionStage GetCurrentStage(string bookId)
    {
        var recentScores = new List<double>();
        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT score
                FROM comprehension_questions
                WHERE book_id = $bookId AND score IS NOT NULL
                ORDER BY answered_at_utc DESC
                LIMIT 5;
                """;
            cmd.Parameters.AddWithValue("$bookId", bookId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                recentScores.Add(r.GetDouble(0));
            }
        }

        if (recentScores.Count < 5)
        {
            return ComprehensionStage.MultipleChoice;
        }

        double avg = recentScores.Average();
        if (avg >= 0.80)
        {
            return ComprehensionStage.FreeForm;
        }
        if (avg >= 0.75)
        {
            return ComprehensionStage.GuidedShortAnswer;
        }
        return ComprehensionStage.MultipleChoice;
    }

    public void RecordHistoricalScoreForTesting(string bookId, ComprehensionStage stage, double normalizedScore)
    {
        string now = DateTime.UtcNow.ToString("O");
        string sessionId = $"sess-{Guid.NewGuid():N}";
        string questionId = $"q-{Guid.NewGuid():N}";
        string mode = StageToModeString(stage);

        using var conn = _db.OpenConnection();
        using (var sCmd = conn.CreateCommand())
        {
            sCmd.CommandText = """
                INSERT INTO comprehension_sessions (session_id, book_id, chapter_index, start_token_id, end_token_id, passage_text, created_at_utc)
                VALUES ($sid, $bid, 1, 0, 100, 'test', $now);
                """;
            sCmd.Parameters.AddWithValue("$sid", sessionId);
            sCmd.Parameters.AddWithValue("$bid", bookId);
            sCmd.Parameters.AddWithValue("$now", now);
            sCmd.ExecuteNonQuery();
        }

        using (var qCmd = conn.CreateCommand())
        {
            qCmd.CommandText = """
                INSERT INTO comprehension_questions (
                    question_id, session_id, book_id, question_mode, prompt_text, prompt_html,
                    options_json, correct_option_idx, rubric_json, user_answer, score, feedback_json, answered_at_utc
                ) VALUES (
                    $qid, $sid, $bid, $mode, 'prompt', 'prompt', '[]', 0, '{}', 'ans', $score, '{}', $now
                );
                """;
            qCmd.Parameters.AddWithValue("$qid", questionId);
            qCmd.Parameters.AddWithValue("$sid", sessionId);
            qCmd.Parameters.AddWithValue("$bid", bookId);
            qCmd.Parameters.AddWithValue("$mode", mode);
            qCmd.Parameters.AddWithValue("$score", Math.Clamp(normalizedScore, 0.0, 1.0));
            qCmd.Parameters.AddWithValue("$now", now);
            qCmd.ExecuteNonQuery();
        }
    }

    public async Task<ComprehensionQuestionView> GenerateQuestionAsync(
        string bookId,
        ComprehensionStage? overrideStage = null,
        int? previousCheckpointTokenId = null,
        int? overrideViewportTokenId = null,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        CancellationToken ct = default)
    {
        var stage = overrideStage ?? GetCurrentStage(bookId);
        string modeStr = StageToModeString(stage);

        var slice = await _readerContext.GetJustReadPassageAsync(
            bookId: bookId,
            maxCharacters: 3500,
            previousCheckpointTokenId: previousCheckpointTokenId,
            overrideViewportTokenId: overrideViewportTokenId,
            overrideChapterIndex: overrideChapterIndex,
            overrideSpineHref: overrideSpineHref,
            ct: ct).ConfigureAwait(false);

        string taskKind = stage switch
        {
            ComprehensionStage.MultipleChoice => "quiz.generate.mcq",
            ComprehensionStage.GuidedShortAnswer => "quiz.generate.guided",
            _ => "quiz.generate.freeform"
        };

        string systemPrompt = QuizPrompts.SystemPrompt;

        string userPrompt = QuizPrompts.BuildUserPrompt(modeStr, slice.PlainText);
        var request = LlmRequestBuilder.Create()
            .WithSystemPrompt(systemPrompt)
            .WithUserPrompt(userPrompt)
            .WithTemperature(0.2)
            .WithTaskKind(taskKind)
            .Build();

        var llmResp = await _llmProvider.CompleteStructuredAsync<GeneratedQuestionDto>(request, ct).ConfigureAwait(false);

        var dto = llmResp.Value;
        var enrichedPrompt = _snippetEnricher.EnrichSnippet(bookId, dto.QuestionPrompt);

        var enrichedOptions = new List<EnrichedQuizOption>();
        if (stage == ComprehensionStage.MultipleChoice)
        {
            var rawOpts = dto.Options is { Count: > 0 }
                ? dto.Options
                : [dto.EvidenceQuote, "別の場面の説明", "登場しない人物の行動", "まだ起きていない出来事"];

            for (int i = 0; i < rawOpts.Count; i++)
            {
                var optEnriched = _snippetEnricher.EnrichSnippet(bookId, rawOpts[i]);
                enrichedOptions.Add(new EnrichedQuizOption(
                    OptionIndex: i,
                    Text: rawOpts[i],
                    EnrichedHtml: optEnriched.EnrichedHtml,
                    SyntheticChapterHref: optEnriched.SyntheticChapterHref));
            }
        }

        int sourceParagraphId = slice.Paragraphs.Count > 0
            ? (slice.Paragraphs.Any(p => p.ParagraphId == dto.ParagraphId) ? dto.ParagraphId : slice.Paragraphs[0].ParagraphId)
            : dto.ParagraphId;

        string sessionId = $"sess-{Guid.NewGuid():N}";
        string questionId = $"q-{Guid.NewGuid():N}";
        string now = DateTime.UtcNow.ToString("O");

        var rubricObj = new
        {
            evidenceQuote = dto.EvidenceQuote,
            paragraphId = sourceParagraphId,
            explanation = dto.Explanation ?? "",
            modelAnswer = dto.ModelAnswer ?? dto.EvidenceQuote,
            passageText = slice.PlainText
        };

        using (var conn = _db.OpenConnection())
        {
            using (var sCmd = conn.CreateCommand())
            {
                sCmd.CommandText = """
                    INSERT INTO comprehension_sessions (
                        session_id, book_id, chapter_index, start_token_id, end_token_id, passage_text, created_at_utc
                    ) VALUES (
                        $sid, $bid, $chIdx, $startTok, $endTok, $text, $now
                    );
                    """;
                sCmd.Parameters.AddWithValue("$sid", sessionId);
                sCmd.Parameters.AddWithValue("$bid", bookId);
                sCmd.Parameters.AddWithValue("$chIdx", slice.ChapterIndex);
                sCmd.Parameters.AddWithValue("$startTok", slice.StartTokenId);
                sCmd.Parameters.AddWithValue("$endTok", slice.EndTokenId);
                sCmd.Parameters.AddWithValue("$text", slice.PlainText);
                sCmd.Parameters.AddWithValue("$now", now);
                sCmd.ExecuteNonQuery();
            }

            using (var qCmd = conn.CreateCommand())
            {
                qCmd.CommandText = """
                    INSERT INTO comprehension_questions (
                        question_id, session_id, book_id, question_mode, prompt_text, prompt_html,
                        options_json, correct_option_idx, rubric_json
                    ) VALUES (
                        $qid, $sid, $bid, $mode, $pText, $pHtml, $optsJson, $correctIdx, $rubricJson
                    );
                    """;
                qCmd.Parameters.AddWithValue("$qid", questionId);
                qCmd.Parameters.AddWithValue("$sid", sessionId);
                qCmd.Parameters.AddWithValue("$bid", bookId);
                qCmd.Parameters.AddWithValue("$mode", modeStr);
                qCmd.Parameters.AddWithValue("$pText", dto.QuestionPrompt);
                qCmd.Parameters.AddWithValue("$pHtml", enrichedPrompt.EnrichedHtml);
                qCmd.Parameters.AddWithValue("$optsJson", JsonSerializer.Serialize(enrichedOptions, JsonOpts));
                qCmd.Parameters.AddWithValue("$correctIdx", dto.CorrectOptionIndex);
                qCmd.Parameters.AddWithValue("$rubricJson", JsonSerializer.Serialize(rubricObj, JsonOpts));
                qCmd.ExecuteNonQuery();
            }
        }

        return new ComprehensionQuestionView(
            QuestionId: questionId,
            SessionId: sessionId,
            BookId: bookId,
            Stage: stage,
            QuestionMode: modeStr,
            PromptText: dto.QuestionPrompt,
            PromptHtml: enrichedPrompt.EnrichedHtml,
            PromptSyntheticChapterHref: enrichedPrompt.SyntheticChapterHref,
            Options: enrichedOptions,
            ChapterIndex: slice.ChapterIndex,
            StartTokenId: slice.StartTokenId,
            EndTokenId: slice.EndTokenId,
            SourceParagraphId: sourceParagraphId,
            EvidenceQuote: dto.EvidenceQuote);
    }

    public async Task<ComprehensionEvaluationResult> SubmitAnswerAsync(
        string questionId,
        string userAnswer,
        int? selectedOptionIndex = null,
        CancellationToken ct = default)
    {
        string bookId;
        string modeStr;
        string promptText;
        int correctOptionIdx;
        string rubricJson;

        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT book_id, question_mode, prompt_text, COALESCE(correct_option_idx, 0), rubric_json
                FROM comprehension_questions
                WHERE question_id = $qid;
                """;
            cmd.Parameters.AddWithValue("$qid", questionId);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                throw new KeyNotFoundException($"Comprehension question '{questionId}' not found.");
            }
            bookId = r.GetString(0);
            modeStr = r.GetString(1);
            promptText = r.GetString(2);
            correctOptionIdx = r.GetInt32(3);
            rubricJson = r.GetString(4);
        }

        using var rubricDoc = JsonDocument.Parse(rubricJson);
        var rRoot = rubricDoc.RootElement;
        string evidenceQuote = rRoot.TryGetProperty("evidenceQuote", out var eq) ? (eq.GetString() ?? "") : "";
        int paragraphId = rRoot.TryGetProperty("paragraphId", out var pid) ? pid.GetInt32() : 0;
        string rubricModelAnswer = rRoot.TryGetProperty("modelAnswer", out var ma) ? (ma.GetString() ?? evidenceQuote) : evidenceQuote;
        string passageText = rRoot.TryGetProperty("passageText", out var pt) ? (pt.GetString() ?? "") : "";

        int score100;
        bool isCorrect;
        string missingOrMisunderstood;
        string rawModelAnswer;

        if (modeStr == "mcq")
        {
            int chosen = selectedOptionIndex ?? (int.TryParse(userAnswer, out int parsedIdx) ? parsedIdx : -1);
            isCorrect = chosen == correctOptionIdx;
            score100 = isCorrect ? 100 : 0;
            missingOrMisunderstood = isCorrect ? "" : "選択された選択肢は本文の記述と異なります。";
            rawModelAnswer = rubricModelAnswer;
        }
        else
        {
            string gradeSystemPrompt = GradingPrompts.SystemPrompt;
            string gradeUserPrompt = GradingPrompts.BuildUserPrompt(promptText, userAnswer, evidenceQuote, passageText);

            var gradeRequest = LlmRequestBuilder.Create()
                .WithSystemPrompt(gradeSystemPrompt)
                .WithUserPrompt(gradeUserPrompt)
                .WithTemperature(0.1)
                .WithTaskKind("quiz.grade.freeform")
                .Build();

            var gradeResp = await _llmProvider.CompleteStructuredAsync<GradedFreeFormDto>(gradeRequest, ct).ConfigureAwait(false);

            var g = gradeResp.Value;
            score100 = Math.Clamp(g.ComprehensionScore, 0, 100);
            isCorrect = score100 >= 70;
            if (!string.IsNullOrWhiteSpace(g.EvidenceQuote))
            {
                evidenceQuote = g.EvidenceQuote;
            }
            missingOrMisunderstood = g.MissingOrMisunderstood ?? "";
            rawModelAnswer = !string.IsNullOrWhiteSpace(g.ModelAnswer) ? g.ModelAnswer : rubricModelAnswer;
        }

        var enrichedModelAnswer = _snippetEnricher.EnrichSnippet(bookId, rawModelAnswer);
        double normalizedScore = score100 / 100.0;
        string now = DateTime.UtcNow.ToString("O");

        var feedbackObj = new
        {
            score100,
            evidenceQuote,
            paragraphId,
            missingOrMisunderstood,
            modelAnswerHtml = enrichedModelAnswer.EnrichedHtml
        };

        using (var conn = _db.OpenConnection())
        using (var upd = conn.CreateCommand())
        {
            upd.CommandText = """
                UPDATE comprehension_questions
                SET user_answer = $ans,
                    score = $score,
                    feedback_json = $fb,
                    answered_at_utc = $now
                WHERE question_id = $qid;
                """;
            upd.Parameters.AddWithValue("$ans", userAnswer ?? selectedOptionIndex?.ToString() ?? "");
            upd.Parameters.AddWithValue("$score", normalizedScore);
            upd.Parameters.AddWithValue("$fb", JsonSerializer.Serialize(feedbackObj, JsonOpts));
            upd.Parameters.AddWithValue("$now", now);
            upd.Parameters.AddWithValue("$qid", questionId);
            upd.ExecuteNonQuery();
        }

        var nextStage = GetCurrentStage(bookId);
        return new ComprehensionEvaluationResult(
            QuestionId: questionId,
            QuestionMode: modeStr,
            IsCorrect: isCorrect,
            ComprehensionScore: score100,
            EvidenceQuote: evidenceQuote,
            ParagraphId: paragraphId,
            JumpToParagraphLabel: $"[Jump to Paragraph #{paragraphId}]",
            MissingOrMisunderstood: missingOrMisunderstood,
            ModelAnswerHtml: enrichedModelAnswer.EnrichedHtml,
            ModelAnswerSyntheticChapterHref: enrichedModelAnswer.SyntheticChapterHref,
            NextRecommendedStage: nextStage);
    }

    private static string StageToModeString(ComprehensionStage stage) => stage switch
    {
        ComprehensionStage.MultipleChoice => "mcq",
        ComprehensionStage.GuidedShortAnswer => "guided",
        _ => "freeform"
    };
}

