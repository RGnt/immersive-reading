using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.AiCompanion;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>The quiz: which passage is asked about, what makes a question acceptable, how a written answer is graded, and how words move.</summary>
public class QuizLogicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quiz-tests-" + Guid.NewGuid().ToString("N"));

    public QuizLogicTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ passage selection
    private static List<ParagraphSlice> Paragraphs(int count, int lengthEach)
        => Enumerable.Range(0, count).Select(i => new ParagraphSlice(i, i * 100, i * 100 + 99, $"段落{i}" + new string('あ', lengthEach))).ToList();

    [Fact]
    public void AShortChapterIsUsedWhole_WithNumberedParagraphsForTheModel()
    {
        var passage = QuizPassageSelector.Select(Paragraphs(3, 50), 2800, new Random(1));

        Assert.Equal(3, passage.Paragraphs.Count);
        Assert.StartsWith("[0] 段落0", passage.PromptText);
        Assert.DoesNotContain("[0]", passage.PlainText);                        // the numbers are for the prompt only
        Assert.Equal((0, 299), (passage.StartTokenId, passage.EndTokenId));
    }

    [Fact]
    public void ALongChapterGivesAWindowOfWholeParagraphsThatFitsTheBudget_AndAvoidsRecentWindows()
    {
        var paragraphs = Paragraphs(12, 400);                                    // ~4,100 chars: windows of 6 paragraphs at 2,800
        var seen = new HashSet<int>();
        for (int seed = 0; seed < 40; seed++)
        {
            var p = QuizPassageSelector.Select(paragraphs, 2800, new Random(seed));
            Assert.InRange(p.PlainText.Length, 1, 2800);
            Assert.Equal(p.Paragraphs.Count, p.Paragraphs.Select(x => x.ParagraphId).Distinct().Count());
            seen.Add(p.Paragraphs[0].ParagraphId);
        }
        Assert.True(seen.Count > 2, "windows should vary between quizzes");

        // the window of the previous quiz is not repeated while another window exists
        var recent = new List<(int Start, int End)> { (0, 5 * 100 + 99) };
        for (int seed = 0; seed < 40; seed++)
            Assert.True(QuizPassageSelector.Select(paragraphs, 2800, new Random(seed), recent).StartTokenId > 0);
    }

    [Fact]
    public void AParagraphLongerThanTheBudgetIsCutAtASentenceEnd()
    {
        string sentence = new string('あ', 300) + "。";
        var p = QuizPassageSelector.Select([new ParagraphSlice(0, 0, 9, string.Concat(Enumerable.Repeat(sentence, 20)))], 1000, new Random(1));

        Assert.True(p.PlainText.Length <= 1000);
        Assert.EndsWith("。", p.PlainText);
    }

    // ------------------------------------------------------------------ question validation
    private static readonly QuizPassage Chapter = QuizPassageSelector.Select(
    [
        new ParagraphSlice(4, 0, 9, "クークは海辺で焚き火の準備を始めました。ミミは流木を集めました。"),
        new ParagraphSlice(5, 10, 19, "夕方になると、二人は火を囲んで魚を焼きました。")
    ], 2800, new Random(1));

    private static GeneratedQuestionDto Mcq(string[]? options = null, int correct = 0, string evidence = "夕方になると、二人は火を囲んで魚を焼きました。", string prompt = "夕方、二人は何をしましたか？")
        => new(prompt, options ?? ["火を囲んで魚を食べた", "海で泳いで遊んだ", "テントを片付けて帰った", "町へ買い物に出かけた"], correct, evidence, 99);

    [Fact]
    public void AGoodQuestionIsAcceptedAndTheCorrectAnswerMovesToARandomPosition()
    {
        var positions = new HashSet<int>();
        for (int seed = 0; seed < 60; seed++)
        {
            var (q, problem) = QuizQuestionValidator.Validate(Mcq(), Chapter, multipleChoice: true, new Random(seed));
            Assert.Null(problem);
            Assert.Equal("火を囲んで魚を食べた", q!.Options[q.CorrectIndex]);
            Assert.Equal(4, q.Options.Distinct().Count());
            Assert.Equal(5, q.ParagraphId);                                          // the paragraph that really holds the evidence, not the model's id 99
            positions.Add(q.CorrectIndex);
        }
        Assert.Equal(4, positions.Count);                                            // all four positions occur
    }

    [Theory]
    [InlineData("evidence is invented", "この文は本文にありません、絶対にありません。", "not found in the passage")]
    [InlineData("evidence too short", "火", "missing or too short")]
    public void EvidenceThatIsNotInThePassageIsRefused(string why, string evidence, string expected)
    {
        var (q, problem) = QuizQuestionValidator.Validate(Mcq(evidence: evidence), Chapter, true, new Random(1));

        Assert.Null(q);
        Assert.Contains(expected, problem);
        Assert.NotEmpty(why);
    }

    [Fact]
    public void ABadOptionSetIsRefusedWithTheReason()
    {
        string Problem(GeneratedQuestionDto dto) => QuizQuestionValidator.Validate(dto, Chapter, true, new Random(1)).Problem!;

        Assert.Contains("exactly 4", Problem(Mcq(["a", "b", "c"])));
        Assert.Contains("exactly 4", Problem(Mcq(["a", "b", "c", "d", "e"])));
        Assert.Contains("0, 1, 2 or 3", Problem(Mcq(correct: 4)));
        Assert.Contains("say the same thing", Problem(Mcq(["火を囲んだ", "火を　囲んだ", "海で泳いだ", "町へ行った"])));
        Assert.Contains("not allowed", Problem(Mcq(["火を囲んで魚を食べた", "海で泳いで遊んだ", "上のすべて", "町へ買い物に出かけた"])));
        Assert.Contains("much longer", Problem(Mcq(["二人は夕方になってから、火を囲んで魚を焼いて食べながら、ゆっくりと話をしていた", "海で泳いだ", "町へ行った", "家に帰った"])));
        Assert.Contains("copies the evidence", Problem(Mcq(["夕方になると、二人は火を囲んで魚を焼きました。", "海で泳いで遊ぶ夕方でした。", "町へ買い物に出かけた夕方", "家で寝てしまった夕方"])));
    }

    [Fact]
    public void AFreeFormQuestionNeedsNoOptionsButStillNeedsRealEvidence()
    {
        var ok = QuizQuestionValidator.Validate(Mcq([], 0), Chapter, multipleChoice: false, new Random(1));
        var bad = QuizQuestionValidator.Validate(Mcq([], 0, evidence: "本文にはない長い文章ですよね。"), Chapter, false, new Random(1));

        Assert.Empty(ok.Question!.Options);
        Assert.Null(bad.Question);
    }

    // ------------------------------------------------------------------ the rubric
    private static FreeFormRubricDto Rubric(int comprehension, int grammar = 80, int vocabulary = 60)
        => new()
        {
            Comprehension = new RubricComprehensionDto { Score = comprehension, EvidenceQuote = "夕方になると、二人は火を囲んで魚を焼きました。", Missing = "" },
            Grammar = new RubricGrammarDto
            {
                Score = grammar,
                Issues = [new RubricGrammarIssueDto { Original = "魚を焼いた", Correction = "魚を焼きました", Note = "丁寧体に統一" },
                          new RubricGrammarIssueDto { Original = "この間違いは書かれていない", Correction = "x" }]
            },
            Vocabulary = new RubricVocabularyDto
            {
                Score = vocabulary,
                WellUsed = [new RubricWordDto { Surface = "火を囲んで", Lemma = "囲む" }, new RubricWordDto { Surface = "でたらめ", Lemma = "でたらめ" }],
                Misused = [new RubricWordDto { Surface = "焼いた", Lemma = "焼く", Suggestion = "焼きました" }]
            }
        };

    private const string Answer = "二人は火を囲んで魚を焼いた。";

    [Fact]
    public void TheRubricIsJudgedInOrder_AndOnlyWhatTheLearnerWroteCounts()
    {
        var r = FreeFormRubricScorer.Score(Rubric(90, 80, 60), Answer, "");

        Assert.True(r.Counted);
        Assert.Equal(90, r.Comprehension);
        Assert.Equal(["魚を焼いた"], r.GrammarIssues.Select(i => i.Original).ToArray());   // the issue that is not in the answer is dropped
        Assert.Equal(["囲む"], r.WellUsed.Select(w => w.Lemma).ToArray());                // so is the word the learner never wrote
        Assert.Equal(["焼く"], r.Misused.Select(w => w.Lemma).ToArray());
        Assert.Equal((int)Math.Round(0.6 * 90 + 0.25 * 80 + 0.15 * 60), r.Overall);
    }

    [Fact]
    public void AnAnswerThatDoesNotAnswerTheQuestionEarnsNeitherGrammarNorVocabularyNorAnyNudge()
    {
        var r = FreeFormRubricScorer.Score(Rubric(25, 95, 95), Answer, "");

        Assert.False(r.Counted);
        Assert.Null(r.Grammar);
        Assert.Null(r.Vocabulary);
        Assert.Equal(25, r.Overall);                                                      // only comprehension is scored
        Assert.Empty(FreeFormRubricScorer.Practice(r));                                   // a fluent answer to another question moves no word
    }

    [Fact]
    public void AWordCannotBeBothWellUsedAndMisused_AndScoresAreClamped()
    {
        var dto = Rubric(150, -5, 300);
        dto.Vocabulary!.WellUsed = [new RubricWordDto { Surface = "焼いた", Lemma = "焼く" }];

        var r = FreeFormRubricScorer.Score(dto, Answer, "");

        Assert.Equal(100, r.Comprehension);
        Assert.Equal((0, 100), (r.Grammar, r.Vocabulary));
        Assert.Empty(r.WellUsed);                                                         // the misuse wins
        Assert.Equal([new VocabularyPractice("焼く", false)], FreeFormRubricScorer.Practice(r).Where(p => p.Lemma == "焼く").ToArray());
    }

    // ------------------------------------------------------------------ nudging the vocabulary state
    private (VocabularyBudgetService Service, VocabularyTracker Tracker) Vocabulary()
    {
        var db = new LibraryDatabase(Path.Combine(_dir, "library.db"), Path.Combine(_dir, "covers"));
        db.InitializeSchema();
        return (new VocabularyBudgetService(db), new VocabularyTracker(db));
    }

    private static IReadOnlyList<VocabularyPractice> Used(string lemma, bool correct) => [new VocabularyPractice(lemma, correct)];

    [Fact]
    public void ThreeCorrectUsesInARowPromoteALearningWord_OneDoesNot_AndAMisuseBreaksTheRun()
    {
        var (service, tracker) = Vocabulary();
        tracker.SetStatus("ja", "囲む", "かこむ", WordStatus.Learning);

        Assert.Empty(service.RecordPractice("ja", Used("囲む", true)));
        Assert.Empty(service.RecordPractice("ja", Used("囲む", true)));
        service.RecordPractice("ja", Used("囲む", false));                                // misused in between: the run starts again
        Assert.Empty(service.RecordPractice("ja", Used("囲む", true)));
        Assert.Empty(service.RecordPractice("ja", Used("囲む", true)));
        var promoted = Assert.Single(service.RecordPractice("ja", Used("囲む", true)));

        Assert.Equal((WordStatus.Learning, WordStatus.Known), (promoted.From, promoted.To));
        Assert.Equal(WordStatus.Known, tracker.GetStatus("ja", "囲む", "かこむ"));
    }

    [Fact]
    public void AMisusedKnownWordGoesBackToLearning_AWordNotInTheVocabularyOnlyCollectsEvidence_AndIgnoredWordsStayIgnored()
    {
        var (service, tracker) = Vocabulary();
        tracker.SetStatus("ja", "焼く", "やく", WordStatus.Known);
        tracker.SetStatus("ja", "無視", "むし", WordStatus.Ignored);

        var demoted = Assert.Single(service.RecordPractice("ja", Used("焼く", false)));
        Assert.Equal((WordStatus.Known, WordStatus.Learning), (demoted.From, demoted.To));
        Assert.Equal(WordStatus.Learning, tracker.GetStatus("ja", "焼く", "やく"));

        Assert.Empty(service.RecordPractice("ja", Used("見知らぬ語", false)));
        Assert.Empty(service.RecordPractice("ja", Used("無視", false)));
        Assert.Equal(WordStatus.Ignored, tracker.GetStatus("ja", "無視", "むし"));
    }

    [Fact]
    public void ACorrectAndAWrongUseOfTheSameWordInOneAnswerCountsAsAMisuse()
    {
        var (service, tracker) = Vocabulary();
        tracker.SetStatus("ja", "焼く", "やく", WordStatus.Known);

        var nudges = service.RecordPractice("ja", [new VocabularyPractice("焼く", true), new VocabularyPractice("焼く", false)]);

        Assert.Equal(WordStatus.Learning, Assert.Single(nudges).To);
    }
}
