using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Dictionaries.Lookup;

internal static class SentenceExtractor
{
    public static (string SentenceText, IReadOnlyList<RubyUnit> SentenceRuby) ExtractSentence(
        ChapterTokenMap map,
        EnrichedTokenEntry tapped)
    {
        var paraTokens = map.GetTokensInParagraph(tapped.ParagraphId).ToList();
        int tappedIdx = paraTokens.FindIndex(t => t.TokenId == tapped.TokenId);
        if (tappedIdx < 0)
        {
            return (tapped.Token.Surface, tapped.RubyUnits);
        }

        // Find sentence boundaries: punctuation ('。', '！', '？', '…')
        int startIdx = tappedIdx;
        while (startIdx > 0)
        {
            var prevSurf = paraTokens[startIdx - 1].Token.Surface;
            if (prevSurf.Contains('。') || prevSurf.Contains('！') || prevSurf.Contains('？'))
            {
                break;
            }
            startIdx--;
        }

        int endIdx = tappedIdx;
        while (endIdx < paraTokens.Count - 1)
        {
            var currSurf = paraTokens[endIdx].Token.Surface;
            if (currSurf.Contains('。') || currSurf.Contains('！') || currSurf.Contains('？'))
            {
                break;
            }
            endIdx++;
        }

        var sentenceTokens = paraTokens.GetRange(startIdx, endIdx - startIdx + 1);
        var text = string.Concat(sentenceTokens.Select(t => t.Token.Surface));
        var rubies = new List<RubyUnit>();

        int curOffset = 0;
        foreach (var t in sentenceTokens)
        {
            foreach (var r in t.RubyUnits)
            {
                rubies.Add(new RubyUnit(
                    RelativeStart: curOffset + r.RelativeStart,
                    RelativeEnd: curOffset + r.RelativeEnd,
                    BaseText: r.BaseText,
                    Reading: r.Reading,
                    Source: r.Source
                ));
            }
            curOffset += t.Token.Surface.Length;
        }

        return (text, rubies);
    }
}

