namespace ImmersiveReading.Core.Extensions;

public sealed record ReaderUiPanelDescriptor(
    string PanelId,
    string Title,
    string Icon,
    string Location = "reader-drawer"
);

public sealed record ParagraphSlice(
    int ParagraphId,
    int StartTokenId,
    int EndTokenId,
    string Text
);

public sealed record ReadPassageSlice(
    string BookId,
    string BookTitle,
    int ChapterIndex,
    string ChapterTitle,
    string SpineHref,
    int StartTokenId,
    int EndTokenId,
    string PlainText,
    IReadOnlyList<ParagraphSlice> Paragraphs
);

public sealed record EnrichedSnippetResult(
    string SnippetId,
    string SyntheticChapterHref,
    string EnrichedHtml,
    int TokenCount,
    IReadOnlyList<string> HighlightedPlusOneLemmas
);

public sealed record VocabularyAuditResult(
    IReadOnlyList<string> UsedPlusOneLemmas,
    IReadOnlyList<string> OutOfBudgetLemmas,
    int TotalContentWords,
    int KnownOrTargetContentWords,
    double KnownRatio
);

public sealed record NPlusOneVocabularyProfile(
    string Language,
    string? BookId,
    IReadOnlySet<string> KnownLemmas,
    IReadOnlySet<string> TargetPlusOneLemmas,
    IReadOnlyList<string> SelectedTurnTargets
);

