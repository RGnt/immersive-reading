using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries.Lookup;

namespace ImmersiveReading.Dictionaries;

/// <summary>
/// Coordinates a dictionary lookup: candidate generation, term/meta/kanji queries, ranking and sentence extraction.
/// Each step lives in a dedicated component under <c>Lookup/</c>.
/// </summary>
public sealed class DictionaryLookupEngine
{
    private readonly IChapterTokenMapProvider _cache;
    private readonly IVocabularyService? _vocabTracker;
    private readonly IDeinflector _deinflector;
    private readonly TermRepository _repository;

    private sealed class NullDeinflector : IDeinflector
    {
        public static readonly NullDeinflector Instance = new();
        public IReadOnlyList<DeinflectionCandidate> Deinflect(ReadOnlySpan<char> surface) => [];
    }

    public DictionaryLookupEngine(
        DictionaryDatabase dictDb,
        IChapterTokenMapProvider cache,
        IVocabularyService? vocabTracker = null,
        IDeinflector? deinflector = null)
    {
        _cache = cache;
        _vocabTracker = vocabTracker;
        _deinflector = deinflector ?? NullDeinflector.Instance;
        _repository = new TermRepository(dictDb);
    }

    public Task<LookupResult?> QueryAsync(LookupQuery query, CancellationToken ct = default)
    {
        var tokenMap = _cache.Get(query.BookId, query.ChapterHref);
        var tappedEntry = tokenMap?.GetToken(query.TokenId);
        if (tokenMap is null || tappedEntry is null)
        {
            return Task.FromResult<LookupResult?>(null);
        }

        var windowTokens = tokenMap.GetForwardWindowTokens(query.TokenId, maxChars: 16);
        if (windowTokens.Count == 0)
        {
            windowTokens = [tappedEntry];
        }

        var candidates = LookupCandidateGenerator.GenerateCandidates(tappedEntry, windowTokens, _deinflector);
        if (candidates.Count == 0)
        {
            return Task.FromResult<LookupResult?>(null);
        }

        var tapped = tappedEntry.Token;
        var terms = _repository.QueryTermsFromDb(candidates.Keys.ToList(), ct);

        string? authorRubyNorm = null;
        if (tapped.ReadingSource == RubySource.EpubAuthor && !string.IsNullOrEmpty(tapped.Reading))
        {
            authorRubyNorm = KanaNormalizer.ToHiragana(tapped.Reading);
        }

        var metaMap = _repository.QueryTermMetaFromDb(terms.Select(t => t.Expression).Distinct().ToList(), ct);
        var ranked = TermRanker.GroupAndRank(terms, candidates, metaMap, tappedEntry, authorRubyNorm, _vocabTracker);

        var top = ranked[0];
        int highlightStart = tappedEntry.TokenId;
        int highlightEnd = top.EndTokenId;

        var matchedTokens = tokenMap.Tokens
            .Where(t => t.ParagraphId == tappedEntry.ParagraphId && t.TokenId >= highlightStart && t.TokenId <= highlightEnd)
            .ToList();
        var matchedSurface = matchedTokens.Count > 0
            ? string.Concat(matchedTokens.Select(t => t.Token.Surface))
            : top.ViewModel.Expression;

        var kanjiChars = new HashSet<char>();
        foreach (var ch in top.ViewModel.Expression.Concat(matchedSurface))
        {
            if (KanaNormalizer.IsKanji(new System.Text.Rune(ch)))
            {
                kanjiChars.Add(ch);
            }
        }

        var kanjiList = _repository.QueryKanjiFromDb(kanjiChars.Select(c => c.ToString()).ToList(), ct);
        var (sentenceText, sentenceRuby) = SentenceExtractor.ExtractSentence(tokenMap, tappedEntry);

        var result = new LookupResult(
            BookId: query.BookId,
            ChapterHref: query.ChapterHref,
            HighlightStartTokenId: highlightStart,
            HighlightEndTokenId: highlightEnd,
            MatchedSurface: matchedSurface,
            SentenceText: sentenceText,
            SentenceRuby: sentenceRuby,
            Terms: ranked.Select(r => r.ViewModel).Take(query.MaxResults).ToList(),
            Kanji: kanjiList);

        return Task.FromResult<LookupResult?>(result);
    }
}
