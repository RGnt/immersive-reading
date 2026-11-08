using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Dictionaries.Lookup;

internal sealed record TermCandidateGroup(TermEntryViewModel ViewModel, int SpanLength, int Tier, int PriorityOrder, double Score, int EndTokenId);

internal static class TermRanker
{
    public static List<TermCandidateGroup> GroupAndRank(
        List<TermRow> terms,
        Dictionary<string, CandidateMeta> formToCandidateMeta,
        Dictionary<string, List<MetaRow>> metaMap,
        EnrichedTokenEntry tappedEntry,
        string? authorRubyNorm,
        IVocabularyService? vocabTracker)
    {
        var tapped = tappedEntry.Token;
        var termViewModels = new List<TermCandidateGroup>();

        foreach (var group in terms.GroupBy(t => (t.Expression, t.ReadingNorm)))
        {
            var first = group.First();
            var expr = first.Expression;
            var readingNorm = first.ReadingNorm;

            formToCandidateMeta.TryGetValue(expr, out var matchedMeta);
            if (matchedMeta == null && formToCandidateMeta.TryGetValue(readingNorm, out var rMeta))
            {
                matchedMeta = rMeta;
            }

            int spanLength = matchedMeta?.SpanLength ?? expr.Length;
            int endTokenId = matchedMeta?.EndTokenId ?? tappedEntry.TokenId;
            var ruleChain = matchedMeta?.RuleChain ?? [];

            string readingMatchStatus;
            int tier;

            if (authorRubyNorm != null)
            {
                if (readingNorm == authorRubyNorm)
                {
                    readingMatchStatus = "epub_ruby_verified";
                    tier = 1;
                }
                else
                {
                    readingMatchStatus = "ruby_mismatch";
                    tier = 4;
                }
            }
            else if (expr == tapped.Lemma || readingNorm == KanaNormalizer.ToHiragana(tapped.Reading))
            {
                readingMatchStatus = "mecab_matched";
                tier = 2;
            }
            else if (matchedMeta?.MatchKind == "deinflected" || ruleChain.Count > 0)
            {
                readingMatchStatus = "deinflected";
                tier = 3;
            }
            else
            {
                readingMatchStatus = "unverified";
                tier = 3;
            }

            var userStatus = vocabTracker?.GetStatus("ja", expr, first.Reading) ?? WordStatus.New;
            var customGloss = vocabTracker?.GetRecord("ja", expr, first.Reading)?.CustomGloss;

            var pitches = new List<PitchAccentInfo>();
            var freqs = new List<FrequencyBadge>();

            if (metaMap.TryGetValue(expr, out var metas))
            {
                foreach (var m in metas)
                {
                    if (m.Mode == "pitch" && (string.IsNullOrEmpty(m.ReadingNorm) || m.ReadingNorm == readingNorm))
                    {
                        PitchFrequencyParser.ParsePitches(m.DataJson, pitches);
                    }
                    else if (m.Mode == "freq" && (string.IsNullOrEmpty(m.ReadingNorm) || m.ReadingNorm == readingNorm))
                    {
                        PitchFrequencyParser.ParseFreq(m.DictTitle, m.DataJson, freqs);
                    }
                }
            }

            var senseBlocks = new List<DictionarySenseBlock>();
            foreach (var t in group.OrderBy(g => g.PriorityOrder).ThenByDescending(g => g.Score))
            {
                var glossList = t.GlossaryText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var tags = t.DefinitionTags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                senseBlocks.Add(new DictionarySenseBlock(t.DictTitle, glossList, tags));
            }

            var vm = new TermEntryViewModel(
                Expression: expr,
                Reading: first.Reading,
                ReadingNormalized: readingNorm,
                UserStatus: userStatus,
                ReviewedContextGloss: customGloss,
                ReadingMatchStatus: readingMatchStatus,
                InflectionChain: ruleChain,
                IsInBookDeck: tappedEntry.InBookDeck,
                BookStudySetNumber: null,
                Pitches: pitches,
                Frequencies: freqs,
                Senses: senseBlocks
            );

            termViewModels.Add(new TermCandidateGroup(vm, spanLength, tier, first.PriorityOrder, first.Score, endTokenId));
        }

        var ranked = termViewModels
            .OrderByDescending(c => c.SpanLength)
            .ThenBy(c => c.Tier)
            .ThenBy(c => c.PriorityOrder)
            .ThenByDescending(c => c.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            string fallbackExpr = !string.IsNullOrEmpty(tapped.Lemma) ? tapped.Lemma : tapped.Surface;
            string fallbackReading = !string.IsNullOrEmpty(tapped.DictionaryReading)
                ? tapped.DictionaryReading
                : (!string.IsNullOrEmpty(tapped.Reading) ? tapped.Reading : fallbackExpr);
            string fallbackNorm = KanaNormalizer.ToHiragana(fallbackReading);
            var fallbackStatus = vocabTracker?.GetStatus("ja", fallbackExpr, fallbackReading) ?? WordStatus.New;
            var fallbackGloss = vocabTracker?.GetRecord("ja", fallbackExpr, fallbackReading)?.CustomGloss;

            var fallbackSense = new DictionarySenseBlock(
                DictionaryTitle: "MeCab Morphology",
                Glosses:
                [
                    !string.IsNullOrEmpty(fallbackGloss)
                        ? fallbackGloss
                        : $"Surface: {tapped.Surface} • Lemma: {fallbackExpr} ({tapped.PartOfSpeech})"
                ],
                Tags: string.IsNullOrEmpty(tapped.PartOfSpeech) ? [] : [tapped.PartOfSpeech]
            );

            var fallbackVm = new TermEntryViewModel(
                Expression: fallbackExpr,
                Reading: fallbackReading,
                ReadingNormalized: fallbackNorm,
                UserStatus: fallbackStatus,
                ReviewedContextGloss: fallbackGloss,
                ReadingMatchStatus: authorRubyNorm != null ? "epub_ruby_verified" : "mecab_matched",
                InflectionChain: [],
                IsInBookDeck: tappedEntry.InBookDeck,
                BookStudySetNumber: null,
                Pitches: [],
                Frequencies: [],
                Senses: [fallbackSense]
            );

            ranked.Add(new TermCandidateGroup(
                fallbackVm,
                tapped.Surface.Length,
                authorRubyNorm != null ? 1 : 2,
                999,
                0,
                tappedEntry.TokenId));
        }

        return ranked;
    }
}

