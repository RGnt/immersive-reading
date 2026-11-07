using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment;

public sealed record EnrichedTokenEntry(
    int TokenId,
    int ParagraphId,
    AnalyzedToken Token,
    IReadOnlyList<RubyUnit> RubyUnits,
    WordStatus Status = WordStatus.New,
    bool InBookDeck = false
);

public sealed class ChapterTokenMap
{
    private readonly Dictionary<int, EnrichedTokenEntry> _tokensById;

    public string BookId { get; }
    public string ChapterHref { get; }
    public IReadOnlyList<EnrichedTokenEntry> Tokens { get; }
    public IReadOnlyList<string> ParagraphTexts { get; }

    public ChapterTokenMap(
        string bookId,
        string chapterHref,
        IReadOnlyList<EnrichedTokenEntry> tokens,
        IReadOnlyList<string> paragraphTexts)
    {
        BookId = bookId;
        ChapterHref = chapterHref;
        Tokens = tokens;
        ParagraphTexts = paragraphTexts;
        _tokensById = tokens.ToDictionary(t => t.TokenId);
    }

    public EnrichedTokenEntry? GetToken(int tokenId) =>
        _tokensById.GetValueOrDefault(tokenId);

    public IReadOnlyList<EnrichedTokenEntry> GetTokensInParagraph(int paragraphId) =>
        Tokens.Where(t => t.ParagraphId == paragraphId).ToList();

    public IReadOnlyList<EnrichedTokenEntry> GetForwardWindowTokens(int startTokenId, int maxChars = 16)
    {
        if (!_tokensById.TryGetValue(startTokenId, out var startToken))
        {
            return [];
        }

        var result = new List<EnrichedTokenEntry>();
        int charCount = 0;

        foreach (var t in Tokens.Where(t => t.ParagraphId == startToken.ParagraphId && t.TokenId >= startTokenId))
        {
            if (charCount > 0 && charCount + t.Token.Surface.Length > maxChars)
            {
                break;
            }
            result.Add(t);
            charCount += t.Token.Surface.Length;
        }

        return result;
    }
}

