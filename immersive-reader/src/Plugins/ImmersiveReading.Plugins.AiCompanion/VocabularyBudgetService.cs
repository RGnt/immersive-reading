using System.Text;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Library;

namespace ImmersiveReading.Plugins.AiCompanion;

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

    private readonly LibraryDatabase _db;
    private readonly PythonStudyPackBridge? _studyPackBridge;

    public VocabularyBudgetService(LibraryDatabase db, PythonStudyPackBridge? studyPackBridge = null)
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
            cmd.Parameters.AddWithValue("$lang", language);
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

