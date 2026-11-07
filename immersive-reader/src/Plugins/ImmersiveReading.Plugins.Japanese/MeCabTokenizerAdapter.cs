using System.Text;
using ImmersiveReading.Core.Plugins;
using MeCab;

namespace ImmersiveReading.Plugins.Japanese;

/// <summary>
/// MeCab tokenizer adapter parsing IPADic / UniDic node features,
/// merging inflectional auxiliaries, and honoring author EPUB ruby spans.
/// </summary>
public sealed class MeCabTokenizerAdapter : ITextTokenizer
{
    private readonly MeCabTagger _tagger;

    private static readonly HashSet<string> BoundAuxiliaryWords = new(StringComparer.Ordinal)
    {
        "た", "だ", "ない", "れる", "られる", "せる", "させる", "たい", "ます", "て", "で"
    };

    private static readonly HashSet<string> BoundAuxiliaryPos = new(StringComparer.Ordinal)
    {
        "助動詞"
    };

    public MeCabTokenizerAdapter(string? dictionaryDir = null)
    {
        var param = new MeCabParam();
        if (!string.IsNullOrWhiteSpace(dictionaryDir) && Directory.Exists(dictionaryDir))
        {
            param.DicDir = dictionaryDir;
        }
        _tagger = MeCabTagger.Create(param);
    }

    public IReadOnlyList<AnalyzedToken> TokenizeParagraph(
        ReadOnlySpan<char> paragraphText,
        IReadOnlyList<SourceRubySpan> authorRubies)
    {
        if (paragraphText.IsEmpty)
        {
            return [];
        }

        string textStr = paragraphText.ToString();
        var rawNodes = new List<RawMeCabNode>();
        int searchPos = 0;

        foreach (var node in _tagger.ParseToNodes(textStr))
        {
            // Skip BOS / EOS nodes
            if (node.Stat is MeCabNodeStat.Bos or MeCabNodeStat.Eos || string.IsNullOrEmpty(node.Surface))
            {
                continue;
            }

            int foundIdx = textStr.IndexOf(node.Surface, searchPos, StringComparison.Ordinal);
            if (foundIdx < 0)
            {
                foundIdx = searchPos;
            }
            int endIdx = foundIdx + node.Surface.Length;
            searchPos = endIdx;

            string[] features = node.Feature?.Split(',') ?? [];
            string pos = features.Length > 0 ? features[0] : "";
            string posSub1 = features.Length > 1 ? features[1] : "";
            string conjType = features.Length > 4 ? features[4] : "";
            string conjForm = features.Length > 5 ? features[5] : "";
            string lemma = features.Length > 6 && features[6] != "*" ? features[6] : node.Surface;
            string rawReading = features.Length > 7 && features[7] != "*" ? features[7] : node.Surface;
            string reading = KanaNormalizer.ToHiragana(rawReading);

            rawNodes.Add(new RawMeCabNode(
                Start: foundIdx,
                End: endIdx,
                Surface: node.Surface,
                Lemma: lemma,
                Reading: reading,
                Pos: pos,
                PosSub1: posSub1,
                ConjType: conjType,
                ConjForm: conjForm));
        }

        // Merge verb/adjective stems with following bound auxiliaries / particles
        var mergedTokens = new List<AnalyzedToken>();
        int i = 0;
        while (i < rawNodes.Count)
        {
            var node = rawNodes[i];
            bool isVerbOrAdj = node.Pos is "動詞" or "形容詞";

            int start = node.Start;
            int end = node.End;
            var surfSb = new StringBuilder(node.Surface);
            var readSb = new StringBuilder(node.Reading);
            string baseLemma = node.Lemma;
            string dictReading = node.Reading;
            string primaryPos = node.Pos;
            string conjType = node.ConjType;
            string conjForm = node.ConjForm;

            // Merge subsequent bound auxiliaries (e.g. 食べ (動詞) + た (助動詞) -> 食べた)
            while (isVerbOrAdj && (i + 1) < rawNodes.Count)
            {
                var next = rawNodes[i + 1];
                bool isAux = BoundAuxiliaryPos.Contains(next.Pos) ||
                            (next.Pos == "助詞" && (next.Surface is "て" or "で")) ||
                            BoundAuxiliaryWords.Contains(next.Surface);

                if (!isAux) break;

                surfSb.Append(next.Surface);
                readSb.Append(next.Reading);
                end = next.End;
                conjForm = next.ConjForm;
                i++;
            }

            string combinedSurface = surfSb.ToString();
            string combinedReading = readSb.ToString();

            // Author ruby override
            var overlappingAuthor = FindOverlappingAuthorRuby(start, end, authorRubies);
            RubySource rubySource;
            string finalReading = combinedReading;

            if (overlappingAuthor.HasValue)
            {
                rubySource = RubySource.EpubAuthor;
                finalReading = KanaNormalizer.ToHiragana(overlappingAuthor.Value.Reading);
            }
            else if (KanaNormalizer.ContainsKanji(combinedSurface))
            {
                rubySource = RubySource.PluginGenerated;
            }
            else
            {
                rubySource = RubySource.None;
            }

            bool isContentWord = primaryPos is "名詞" or "動詞" or "形容詞" or "副詞" or "連体詞" or "感動詞";

            mergedTokens.Add(new AnalyzedToken(
                StartCharOffset: start,
                EndCharOffset: end,
                Surface: combinedSurface,
                Lemma: baseLemma,
                NormalizedForm: baseLemma,
                Reading: finalReading,
                DictionaryReading: dictReading,
                PartOfSpeech: primaryPos,
                ConjugationType: conjType,
                ConjugationForm: conjForm,
                ReadingSource: rubySource,
                IsContentWord: isContentWord));

            i++;
        }

        return mergedTokens;
    }

    private static SourceRubySpan? FindOverlappingAuthorRuby(
        int tokenStart,
        int tokenEnd,
        IReadOnlyList<SourceRubySpan> authorRubies)
    {
        if (authorRubies is null) return null;
        foreach (var ar in authorRubies)
        {
            if (Math.Max(tokenStart, ar.StartCharOffset) < Math.Min(tokenEnd, ar.EndCharOffset))
            {
                return ar;
            }
        }
        return null;
    }

    private sealed record RawMeCabNode(
        int Start,
        int End,
        string Surface,
        string Lemma,
        string Reading,
        string Pos,
        string PosSub1,
        string ConjType,
        string ConjForm
    );

    public void Dispose()
    {
        _tagger?.Dispose();
    }
}

