using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Core.Extensions;

public interface IDomEnrichmentStage
{
    string StageId { get; }
    int Order { get; }
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
}

