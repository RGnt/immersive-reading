using AngleSharp.Dom;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Core.Extensions;

public sealed record DomEnrichmentContext(
    string BookId,
    string ChapterHref,
    IReadOnlyList<EnrichedTokenEntry> Tokens,
    EnrichmentOptions Options
);

public interface IDomEnrichmentStage
{
    string StageId { get; }
    int Order { get; }
    void Enrich(IDocument document, DomEnrichmentContext context);
}

public interface IReaderContextService
{
    Task<ReadPassageSlice> GetJustReadPassageAsync(
        string bookId,
        int maxCharacters = 3500,
        int? previousCheckpointTokenId = null,
        int? overrideViewportTokenId = null,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ReadPassageSlice>> GetStoryContextUpToProgressAsync(
        string bookId,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        int? overrideViewportTokenId = null,
        CancellationToken ct = default);

    /// <summary>
    /// The whole text of the last chapter the reader has **completed**: the closest non-excluded chapter before the current one
    /// (<paramref name="currentChapterIndex"/>, or the saved reading position). Nothing from the current chapter or later is ever included, so
    /// this is the spoiler gate for anything that asks about the story (quizzes). Null when no chapter has been completed yet.
    /// </summary>
    Task<ReadPassageSlice?> GetLastCompletedChapterAsync(
        string bookId,
        int? currentChapterIndex = null,
        int maxCharacters = 60000,
        CancellationToken ct = default)
        => throw new NotSupportedException("This reader context cannot provide completed chapters.");
}

public interface ISnippetEnricher
{
    EnrichedSnippetResult EnrichSnippet(
        string bookId,
        string htmlOrTextSnippet,
        string languageCode = "ja",
        IReadOnlySet<string>? targetPlusOneLemmas = null);
}

public interface IVocabularyBudgetService
{
    NPlusOneVocabularyProfile BuildNPlusOneProfile(
        string language = "ja",
        string? bookId = null,
        IReadOnlyCollection<string>? chapterTargetWords = null,
        int maxTurnTargets = 5);

    VocabularyAuditResult AuditReplyTokens(
        IReadOnlyList<AnalyzedToken> tokens,
        NPlusOneVocabularyProfile profile);

    /// <summary>
    /// Records how the learner used words in their own writing and moves the status of words they already have in the vocabulary a step in the
    /// right direction: three correct uses in a row promote a word towards Known; a misuse sends a Known word back to Learning and a New word to
    /// Learning. Words without a vocabulary entry only collect evidence. Returns what changed.
    /// </summary>
    IReadOnlyList<VocabularyNudge> RecordPractice(string language, IReadOnlyList<VocabularyPractice> practice) => [];
}

