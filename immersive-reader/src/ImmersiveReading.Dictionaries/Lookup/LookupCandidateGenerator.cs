using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Dictionaries.Lookup;

internal sealed record PrefixCandidate(string Surface, int SpanLength, int EndTokenId, int TokenCount);
internal sealed record CandidateMeta(string Form, int SpanLength, int EndTokenId, IReadOnlyList<string> RuleChain, string MatchKind);

internal static class LookupCandidateGenerator
{
    public static Dictionary<string, CandidateMeta> GenerateCandidates(
        EnrichedTokenEntry tappedEntry,
        IReadOnlyList<EnrichedTokenEntry> windowTokens,
        IDeinflector deinflector)
    {
        // Build candidate prefixes ending strictly on token boundaries
        var prefixCandidates = new List<PrefixCandidate>();
        for (int k = 1; k <= windowTokens.Count; k++)
        {
            var subList = windowTokens.Take(k).ToList();
            var surface = string.Concat(subList.Select(t => t.Token.Surface));
            var endTokenId = subList[^1].TokenId;
            prefixCandidates.Add(new PrefixCandidate(surface, surface.Length, endTokenId, subList.Count));
        }

        var formToCandidateMeta = new Dictionary<string, CandidateMeta>(StringComparer.Ordinal);

        void AddSearchKey(string form, int spanLength, int endTokenId, IReadOnlyList<string> ruleChain, string matchKind)
        {
            if (string.IsNullOrEmpty(form)) return;

            if (!formToCandidateMeta.TryGetValue(form, out var existing) || spanLength > existing.SpanLength)
            {
                formToCandidateMeta[form] = new CandidateMeta(form, spanLength, endTokenId, ruleChain, matchKind);
            }
        }

        // 1. Tapped token lemma and normalized form
        var tapped = tappedEntry.Token;
        if (!string.IsNullOrEmpty(tapped.Lemma))
        {
            AddSearchKey(tapped.Lemma, tapped.Surface.Length, tappedEntry.TokenId, [], "mecab_matched");
        }
        if (!string.IsNullOrEmpty(tapped.NormalizedForm))
        {
            AddSearchKey(tapped.NormalizedForm, tapped.Surface.Length, tappedEntry.TokenId, [], "mecab_matched");
        }

        // 2. Prefix surfaces & deinflections
        foreach (var prefix in prefixCandidates)
        {
            AddSearchKey(prefix.Surface, prefix.SpanLength, prefix.EndTokenId, [], "surface_match");

            var deinflected = deinflector.Deinflect(prefix.Surface);
            foreach (var d in deinflected)
            {
                AddSearchKey(d.BaseForm, prefix.SpanLength, prefix.EndTokenId, d.RuleChain, "deinflected");
            }
        }

        return formToCandidateMeta;
    }
}

