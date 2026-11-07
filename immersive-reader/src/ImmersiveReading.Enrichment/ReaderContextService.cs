using System.Text;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Library;

namespace ImmersiveReading.Enrichment;

public sealed class ReaderContextService : IReaderContextService
{
    private readonly LibraryManager _library;
    private readonly LibraryDatabase? _db;
    private readonly ChapterEnrichmentCache _cache;
    private readonly BookSchemeHandler? _bookSchemeHandler;

    public ReaderContextService(
        LibraryManager library,
        ChapterEnrichmentCache cache,
        LibraryDatabase? db = null,
        BookSchemeHandler? bookSchemeHandler = null)
    {
        _library = library;
        _cache = cache;
        _db = db;
        _bookSchemeHandler = bookSchemeHandler;
    }

    public Task<ReadPassageSlice> GetJustReadPassageAsync(
        string bookId,
        int maxCharacters = 3500,
        int? previousCheckpointTokenId = null,
        int? overrideViewportTokenId = null,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        CancellationToken ct = default)
    {
        var (bookTitle, chapterIndex, chapterTitle, spineHref, locatorTokenId) = ResolveCurrentPosition(
            bookId,
            overrideChapterIndex,
            overrideSpineHref,
            overrideViewportTokenId);

        var tokenMap = ResolveTokenMap(bookId, spineHref);
        if (tokenMap is null || tokenMap.Tokens.Count == 0)
        {
            return Task.FromResult(new ReadPassageSlice(
                BookId: bookId,
                BookTitle: bookTitle,
                ChapterIndex: chapterIndex,
                ChapterTitle: chapterTitle,
                SpineHref: spineHref,
                StartTokenId: 0,
                EndTokenId: 0,
                PlainText: "",
                Paragraphs: []));
        }

        int maxAvailableTokenId = tokenMap.Tokens[^1].TokenId;
        int effectiveEndTokenId = locatorTokenId.HasValue
            ? Math.Clamp(locatorTokenId.Value, 0, maxAvailableTokenId)
            : maxAvailableTokenId;

        int minAllowedTokenId = previousCheckpointTokenId.HasValue
            ? Math.Max(0, previousCheckpointTokenId.Value)
            : 0;

        // Hard Spoiler Gate: NEVER include any token with TokenId > effectiveEndTokenId
        var eligibleTokens = tokenMap.Tokens
            .Where(t => t.TokenId >= minAllowedTokenId && t.TokenId <= effectiveEndTokenId)
            .OrderBy(t => t.TokenId)
            .ToList();

        if (eligibleTokens.Count == 0)
        {
            return Task.FromResult(new ReadPassageSlice(
                BookId: bookId,
                BookTitle: bookTitle,
                ChapterIndex: chapterIndex,
                ChapterTitle: chapterTitle,
                SpineHref: spineHref,
                StartTokenId: minAllowedTokenId,
                EndTokenId: effectiveEndTokenId,
                PlainText: "",
                Paragraphs: []));
        }

        // Walk backward from effectiveEndTokenId up to maxCharacters
        var selectedTokens = new List<EnrichedTokenEntry>();
        int charBudget = 0;
        for (int i = eligibleTokens.Count - 1; i >= 0; i--)
        {
            var tok = eligibleTokens[i];
            int len = tok.Token.Surface.Length;
            if (selectedTokens.Count > 0 && charBudget + len > maxCharacters)
            {
                break;
            }
            selectedTokens.Add(tok);
            charBudget += len;
        }

        selectedTokens.Reverse();

        int actualStartTokenId = selectedTokens[0].TokenId;
        int actualEndTokenId = selectedTokens[^1].TokenId;

        var paragraphs = new List<ParagraphSlice>();
        var plainSb = new StringBuilder();

        foreach (var group in selectedTokens.GroupBy(t => t.ParagraphId).OrderBy(g => g.Key))
        {
            var pTokens = group.ToList();
            string pText = string.Concat(pTokens.Select(t => t.Token.Surface));
            paragraphs.Add(new ParagraphSlice(
                ParagraphId: group.Key,
                StartTokenId: pTokens[0].TokenId,
                EndTokenId: pTokens[^1].TokenId,
                Text: pText));

            if (plainSb.Length > 0) plainSb.Append('\n');
            plainSb.Append(pText);
        }

        return Task.FromResult(new ReadPassageSlice(
            BookId: bookId,
            BookTitle: bookTitle,
            ChapterIndex: chapterIndex,
            ChapterTitle: chapterTitle,
            SpineHref: spineHref,
            StartTokenId: actualStartTokenId,
            EndTokenId: actualEndTokenId,
            PlainText: plainSb.ToString(),
            Paragraphs: paragraphs));
    }

    public async Task<IReadOnlyList<ReadPassageSlice>> GetStoryContextUpToProgressAsync(
        string bookId,
        int? overrideChapterIndex = null,
        string? overrideSpineHref = null,
        int? overrideViewportTokenId = null,
        CancellationToken ct = default)
    {
        var (bookTitle, currentChapterIdx, _, currentSpineHref, currentViewportTokenId) = ResolveCurrentPosition(
            bookId,
            overrideChapterIndex,
            overrideSpineHref,
            overrideViewportTokenId);

        var slices = new List<ReadPassageSlice>();
        var detail = _library.GetBookDetail(bookId);

        if (detail is not null && detail.Chapters.Count > 0)
        {
            // Strictly only chapters <= currentChapterIdx (Spoiler Guard)
            foreach (var ch in detail.Chapters.Where(c => !c.IsExcluded && c.ChapterIndex <= currentChapterIdx).OrderBy(c => c.ChapterIndex))
            {
                bool isCurrentChapter = ch.ChapterIndex == currentChapterIdx;
                int? capToken = isCurrentChapter ? currentViewportTokenId : null;

                var slice = await GetJustReadPassageAsync(
                    bookId: bookId,
                    maxCharacters: isCurrentChapter ? 3500 : 1200,
                    previousCheckpointTokenId: 0,
                    overrideViewportTokenId: capToken,
                    overrideChapterIndex: ch.ChapterIndex,
                    overrideSpineHref: ch.SpineHref,
                    ct: ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(slice.PlainText))
                {
                    slices.Add(slice);
                }
            }
        }
        else
        {
            var singleSlice = await GetJustReadPassageAsync(
                bookId: bookId,
                maxCharacters: 3500,
                previousCheckpointTokenId: 0,
                overrideViewportTokenId: currentViewportTokenId,
                overrideChapterIndex: currentChapterIdx,
                overrideSpineHref: currentSpineHref,
                ct: ct).ConfigureAwait(false);
            slices.Add(singleSlice);
        }

        return slices;
    }

    private ChapterTokenMap? ResolveTokenMap(string bookId, string spineHref)
    {
        if (string.IsNullOrWhiteSpace(spineHref)) return null;
        var cached = _cache.Get(bookId, spineHref);
        if (cached is not null) return cached;

        return _bookSchemeHandler?.GetEnrichedChapter(bookId, spineHref)?.TokenMap;
    }

    private (string BookTitle, int ChapterIndex, string ChapterTitle, string SpineHref, int? LocatorTokenId) ResolveCurrentPosition(
        string bookId,
        int? overrideChapterIndex,
        string? overrideSpineHref,
        int? overrideViewportTokenId)
    {
        var detail = _library.GetBookDetail(bookId);
        string bookTitle = detail?.Summary.Title ?? bookId;

        int chapterIdx = overrideChapterIndex ?? 0;
        string spineHref = overrideSpineHref ?? "";
        int? locatorTokenId = overrideViewportTokenId;

        if (_db is not null && (overrideChapterIndex is null || string.IsNullOrWhiteSpace(overrideSpineHref) || overrideViewportTokenId is null))
        {
            using var conn = _db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_chapter_index, current_spine_href, locator_token_id FROM reading_progress WHERE book_id = $id;";
            cmd.Parameters.AddWithValue("$id", bookId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                if (overrideChapterIndex is null) chapterIdx = r.GetInt32(0);
                if (string.IsNullOrWhiteSpace(overrideSpineHref)) spineHref = r.GetString(1);
                if (overrideViewportTokenId is null && !r.IsDBNull(2)) locatorTokenId = r.GetInt32(2);
            }
        }

        if (string.IsNullOrWhiteSpace(spineHref) && detail is not null && detail.Chapters.Count > 0)
        {
            var ch = detail.Chapters.FirstOrDefault(c => c.ChapterIndex == chapterIdx)
                  ?? detail.Chapters.FirstOrDefault(c => !c.IsExcluded)
                  ?? detail.Chapters[0];
            chapterIdx = ch.ChapterIndex;
            spineHref = ch.SpineHref;
        }

        string chapterTitle = detail?.Chapters.FirstOrDefault(c => c.ChapterIndex == chapterIdx)?.Title
                           ?? $"Chapter {chapterIdx + 1}";

        return (bookTitle, chapterIdx, chapterTitle, spineHref, locatorTokenId);
    }
}
