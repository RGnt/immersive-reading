using System.Data.Common;
using System.Text;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment;

public sealed class VocabularyBudgetService : IVocabularyBudgetService
{
    private static readonly HashSet<string> CoreFoundationalWords = new(StringComparer.Ordinal)
    {
        "する", "いる", "ある", "なる", "れる", "られる", "せる", "させる", "ない", "たい",
        "私", "僕", "俺", "君", "あなた", "これ", "それ", "あれ", "ここ", "そこ", "あそこ",
        "一緒", "いっしょ", "見る", "行く", "来る", "言う", "思う", "食べる", "飲む", "知る",
        "今日", "明日", "昨日", "今", "時", "人", "物", "事", "所", "方", "前", "後", "中",
        "とても", "すごく", "まだ", "もう", "すぐ", "また", "もっと", "ちょっと", "うん", "はい"
    };

    private readonly IDbConnectionFactory _db;
    private readonly IStudyPackCatalog? _studyPackBridge;

    public VocabularyBudgetService(IDbConnectionFactory db, IStudyPackCatalog? studyPackBridge = null)
    {
        _db = db;
        _studyPackBridge = studyPackBridge;
    }

    public NPlusOneVocabularyProfile BuildNPlusOneProfile(
        string language = "ja",
        string? bookId = null,
        IReadOnlyCollection<string>? chapterTargetWords = null,
        int maxTurnTargets = 5)
    {
        var known = new HashSet<string>(CoreFoundationalWords, StringComparer.Ordinal);
        var targetPlusOne = new HashSet<string>(StringComparer.Ordinal);

        using (var conn = _db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT lemma, status FROM vocabulary_state WHERE language = $lang;";
            var p = cmd.CreateParameter();
            p.ParameterName = "$lang";
            p.Value = language;
            cmd.Parameters.Add(p);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string lemma = r.GetString(0);
                var status = (WordStatus)r.GetInt32(1);
                if (status == WordStatus.Known)
                {
                    known.Add(lemma);
                }
                else if (status == WordStatus.Learning)
                {
                    targetPlusOne.Add(lemma);
                }
            }
        }

        if (chapterTargetWords is not null)
        {
            foreach (var w in chapterTargetWords)
            {
                if (!string.IsNullOrWhiteSpace(w) && !known.Contains(w.Trim()))
                {
                    targetPlusOne.Add(w.Trim());
                }
            }
        }

        if (targetPlusOne.Count < maxTurnTargets && !string.IsNullOrWhiteSpace(bookId) && _studyPackBridge is not null)
        {
            var pack = _studyPackBridge.GetBookStudyPack(bookId);
            foreach (var set in pack.VocabularySets.Take(2))
            {
                foreach (var w in set.Words)
                {
                    if (!string.IsNullOrWhiteSpace(w.Word) && !known.Contains(w.Word))
                    {
                        targetPlusOne.Add(w.Word);
                    }
                }
            }
        }

        var selectedTargets = targetPlusOne.Take(Math.Max(1, maxTurnTargets)).ToList();
        return new NPlusOneVocabularyProfile(
            Language: language,
            BookId: bookId,
            KnownLemmas: known,
            TargetPlusOneLemmas: targetPlusOne,
            SelectedTurnTargets: selectedTargets);
    }

    /// <summary>Correct uses in a row needed before a word is promoted.</summary>
    public const int PromoteAfterCorrectUses = 3;

    public IReadOnlyList<VocabularyNudge> RecordPractice(string language, IReadOnlyList<VocabularyPractice> practice)
    {
        var nudges = new List<VocabularyNudge>();
        // one entry per word; a misuse beats a correct use of the same word in the same answer
        var byLemma = practice.Where(p => !string.IsNullOrWhiteSpace(p.Lemma))
            .GroupBy(p => p.Lemma.Trim(), StringComparer.Ordinal)
            .Select(g => (Lemma: g.Key, Correct: g.All(x => x.Correct))).ToList();
        if (byLemma.Count == 0) return nudges;

        string now = DateTime.UtcNow.ToString("o");
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var (lemma, correct) in byLemma)
        {
            int run;
            using (var up = conn.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO vocabulary_practice (language, lemma, correct_uses, misuses, updated_at_utc)
                    VALUES ($lang, $lemma, $correct, $misuse, $now)
                    ON CONFLICT(language, lemma) DO UPDATE SET
                        correct_uses = CASE WHEN $isCorrect = 1 THEN vocabulary_practice.correct_uses + 1 ELSE 0 END,
                        misuses = vocabulary_practice.misuses + $misuse,
                        updated_at_utc = $now
                    RETURNING correct_uses;
                    """;
                P(up, "$lang", language);
                P(up, "$lemma", lemma);
                P(up, "$correct", correct ? 1 : 0);
                P(up, "$misuse", correct ? 0 : 1);
                P(up, "$isCorrect", correct ? 1 : 0);
                P(up, "$now", now);
                run = Convert.ToInt32(up.ExecuteScalar());
            }

            // the words the learner has in the vocabulary (any reading of the lemma)
            var rows = new List<(string Reading, WordStatus Status)>();
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT reading, status FROM vocabulary_state WHERE language = $lang AND lemma = $lemma;";
                P(sel, "$lang", language);
                P(sel, "$lemma", lemma);
                using var r = sel.ExecuteReader();
                while (r.Read()) rows.Add((r.GetString(0), (WordStatus)r.GetInt32(1)));
            }

            foreach (var (reading, status) in rows)
            {
                WordStatus? to = null;
                string reason = "";
                if (!correct && (status == WordStatus.Known || status == WordStatus.New))
                {
                    to = WordStatus.Learning;
                    reason = status == WordStatus.Known ? "misused in your answer, so it goes back to Learning" : "you tried to use it and it was not right: Learning";
                }
                else if (correct && run >= PromoteAfterCorrectUses && (status == WordStatus.Learning || status == WordStatus.New))
                {
                    to = WordStatus.Known;
                    reason = $"used correctly {run} times in a row";
                }
                if (to is null) continue;

                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE vocabulary_state SET status = $status, updated_at_utc = $now WHERE language = $lang AND lemma = $lemma AND reading = $reading;";
                P(upd, "$status", (int)to.Value);
                P(upd, "$now", now);
                P(upd, "$lang", language);
                P(upd, "$lemma", lemma);
                P(upd, "$reading", reading);
                upd.ExecuteNonQuery();
                nudges.Add(new VocabularyNudge(lemma, status, to.Value, reason));
            }
        }
        tx.Commit();
        return nudges;
    }

    private static void P(DbCommand cmd, string name, object? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(parameter);
    }

    public VocabularyAuditResult AuditReplyTokens(
        IReadOnlyList<AnalyzedToken> tokens,
        NPlusOneVocabularyProfile profile)
    {
        var usedPlusOne = new List<string>();
        var outOfBudget = new List<string>();
        int totalContent = 0;
        int knownOrTargetCount = 0;

        foreach (var tok in tokens)
        {
            if (!tok.IsContentWord)
            {
                continue;
            }

            string lemma = !string.IsNullOrWhiteSpace(tok.Lemma) && tok.Lemma != "*" ? tok.Lemma : tok.Surface;
            string surface = tok.Surface;
            if (string.IsNullOrWhiteSpace(lemma))
            {
                continue;
            }

            totalContent++;

            if (profile.TargetPlusOneLemmas.Contains(lemma) || profile.TargetPlusOneLemmas.Contains(surface))
            {
                knownOrTargetCount++;
                string matchedTarget = profile.TargetPlusOneLemmas.Contains(lemma) ? lemma : surface;
                if (!usedPlusOne.Contains(matchedTarget, StringComparer.Ordinal))
                {
                    usedPlusOne.Add(matchedTarget);
                }
                continue;
            }

            if (profile.KnownLemmas.Contains(lemma) || profile.KnownLemmas.Contains(surface))
            {
                knownOrTargetCount++;
                continue;
            }

            // Pure kana grammatical/conversational words of <= 3 chars that contain no kanji are treated as basic comfort words
            if (!ContainsKanji(surface) && !ContainsKanji(lemma) && surface.Length <= 3)
            {
                knownOrTargetCount++;
                continue;
            }

            if (!outOfBudget.Contains(lemma, StringComparer.Ordinal))
            {
                outOfBudget.Add(lemma);
            }
        }

        double ratio = totalContent == 0 ? 1.0 : (double)knownOrTargetCount / totalContent;
        return new VocabularyAuditResult(
            UsedPlusOneLemmas: usedPlusOne,
            OutOfBudgetLemmas: outOfBudget,
            TotalContentWords: totalContent,
            KnownOrTargetContentWords: knownOrTargetCount,
            KnownRatio: Math.Round(ratio, 4));
    }

    private static bool ContainsKanji(string text)
    {
        foreach (Rune r in text.EnumerateRunes())
        {
            int v = r.Value;
            if ((v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF))
            {
                return true;
            }
        }
        return false;
    }
}

