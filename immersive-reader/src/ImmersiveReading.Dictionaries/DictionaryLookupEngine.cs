using System.Diagnostics;
using System.Text.Json;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.Japanese;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries;

public sealed class DictionaryLookupEngine
{
    private readonly DictionaryDatabase _dictDb;
    private readonly ChapterEnrichmentCache _cache;
    private readonly VocabularyTracker? _vocabTracker;
    private readonly IDeinflector _deinflector;

    public DictionaryLookupEngine(
        DictionaryDatabase dictDb,
        ChapterEnrichmentCache cache,
        VocabularyTracker? vocabTracker = null,
        IDeinflector? deinflector = null)
    {
        _dictDb = dictDb;
        _cache = cache;
        _vocabTracker = vocabTracker;
        _deinflector = deinflector ?? new JapaneseDeinflector();
    }

    public Task<LookupResult?> QueryAsync(LookupQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var tokenMap = _cache.Get(query.BookId, query.ChapterHref);
        if (tokenMap == null)
        {
            return Task.FromResult<LookupResult?>(null);
        }

        var tappedEntry = tokenMap.GetToken(query.TokenId);
        if (tappedEntry == null)
        {
            return Task.FromResult<LookupResult?>(null);
        }

        var windowTokens = tokenMap.GetForwardWindowTokens(query.TokenId, maxChars: 16);
        if (windowTokens.Count == 0)
        {
            windowTokens = [tappedEntry];
        }

        // Build candidate prefixes ending strictly on token boundaries
        var prefixCandidates = new List<PrefixCandidate>();
        for (int k = 1; k <= windowTokens.Count; k++)
        {
            var subList = windowTokens.Take(k).ToList();
            var surface = string.Concat(subList.Select(t => t.Token.Surface));
            var endTokenId = subList[^1].TokenId;
            prefixCandidates.Add(new PrefixCandidate(surface, surface.Length, endTokenId, subList.Count));
        }

        // Gather all expression & reading candidate keys
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

            var deinflected = _deinflector.Deinflect(prefix.Surface);
            foreach (var d in deinflected)
            {
                AddSearchKey(d.BaseForm, prefix.SpanLength, prefix.EndTokenId, d.RuleChain, "deinflected");
            }
        }

        if (formToCandidateMeta.Count == 0)
        {
            return Task.FromResult<LookupResult?>(null);
        }

        // Query dictionary_terms
        var forms = formToCandidateMeta.Keys.ToList();
        var terms = QueryTermsFromDb(forms, ct);

        // Check author ruby
        string? authorRubyNorm = null;
        if (tapped.ReadingSource == RubySource.EpubAuthor && !string.IsNullOrEmpty(tapped.Reading))
        {
            authorRubyNorm = KanaNormalizer.ToHiragana(tapped.Reading);
        }

        // Query metadata (pitch, freq) for matched expressions
        var matchedExpressions = terms.Select(t => t.Expression).Distinct().ToList();
        var metaMap = QueryTermMetaFromDb(matchedExpressions, ct);

        // Group terms by Expression + ReadingNormalized
        var termViewModels = new List<TermCandidateGroup>();

        foreach (var group in terms.GroupBy(t => (t.Expression, t.ReadingNorm)))
        {
            var first = group.First();
            var expr = first.Expression;
            var readingNorm = first.ReadingNorm;

            // Find best matching CandidateMeta
            formToCandidateMeta.TryGetValue(expr, out var matchedMeta);
            if (matchedMeta == null && formToCandidateMeta.TryGetValue(readingNorm, out var rMeta))
            {
                matchedMeta = rMeta;
            }

            int spanLength = matchedMeta?.SpanLength ?? expr.Length;
            int endTokenId = matchedMeta?.EndTokenId ?? tappedEntry.TokenId;
            var ruleChain = matchedMeta?.RuleChain ?? [];

            // Determine ReadingMatchStatus
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

            // User vocabulary status
            var userStatus = _vocabTracker?.GetStatus("ja", expr, first.Reading) ?? WordStatus.New;
            var customGloss = _vocabTracker?.GetRecord("ja", expr, first.Reading)?.CustomGloss;

            // Metadata pitches and frequencies
            var pitches = new List<PitchAccentInfo>();
            var freqs = new List<FrequencyBadge>();

            if (metaMap.TryGetValue(expr, out var metas))
            {
                foreach (var m in metas)
                {
                    if (m.Mode == "pitch" && (string.IsNullOrEmpty(m.ReadingNorm) || m.ReadingNorm == readingNorm))
                    {
                        ParsePitches(m.DataJson, pitches);
                    }
                    else if (m.Mode == "freq" && (string.IsNullOrEmpty(m.ReadingNorm) || m.ReadingNorm == readingNorm))
                    {
                        ParseFreq(m.DictTitle, m.DataJson, freqs);
                    }
                }
            }

            // Senses
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

        // Rank candidates: longest span in text first, then verification tier (Tier 1 author ruby verified > Tier 2 MeCab > Tier 3 deinflected), then priority/score
        var ranked = termViewModels
            .OrderByDescending(c => c.SpanLength)           // 1. Longest matched phrase first (e.g. 気になる before 気)
            .ThenBy(c => c.Tier)                           // 2. Reading verification tier (author ruby verified first)
            .ThenBy(c => c.PriorityOrder)                   // 3. Dictionary priority
            .ThenByDescending(c => c.Score)                 // 4. Dictionary score
            .ToList();

        if (ranked.Count == 0)
        {
            string fallbackExpr = !string.IsNullOrEmpty(tapped.Lemma) ? tapped.Lemma : tapped.Surface;
            string fallbackReading = !string.IsNullOrEmpty(tapped.DictionaryReading)
                ? tapped.DictionaryReading
                : (!string.IsNullOrEmpty(tapped.Reading) ? tapped.Reading : fallbackExpr);
            string fallbackNorm = KanaNormalizer.ToHiragana(fallbackReading);
            var fallbackStatus = _vocabTracker?.GetStatus("ja", fallbackExpr, fallbackReading) ?? WordStatus.New;
            var fallbackGloss = _vocabTracker?.GetRecord("ja", fallbackExpr, fallbackReading)?.CustomGloss;

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

        var top = ranked[0];
        int highlightStart = tappedEntry.TokenId;
        int highlightEnd = top.EndTokenId;

        // Build matched surface
        var matchedTokens = tokenMap.Tokens
            .Where(t => t.ParagraphId == tappedEntry.ParagraphId && t.TokenId >= highlightStart && t.TokenId <= highlightEnd)
            .ToList();
        var matchedSurface = matchedTokens.Count > 0
            ? string.Concat(matchedTokens.Select(t => t.Token.Surface))
            : top.ViewModel.Expression;

        // Extract Kanji cards for characters in top.Expression and matchedSurface
        var kanjiChars = new HashSet<char>();
        foreach (var ch in top.ViewModel.Expression.Concat(matchedSurface))
        {
            if (KanaNormalizer.IsKanji(new System.Text.Rune(ch)))
            {
                kanjiChars.Add(ch);
            }
        }

        var kanjiList = QueryKanjiFromDb(kanjiChars.Select(c => c.ToString()).ToList(), ct);

        // Extract Sentence
        var (sentenceText, sentenceRuby) = ExtractSentence(tokenMap, tappedEntry);

        sw.Stop();

        var result = new LookupResult(
            BookId: query.BookId,
            ChapterHref: query.ChapterHref,
            HighlightStartTokenId: highlightStart,
            HighlightEndTokenId: highlightEnd,
            MatchedSurface: matchedSurface,
            SentenceText: sentenceText,
            SentenceRuby: sentenceRuby,
            Terms: ranked.Select(r => r.ViewModel).Take(query.MaxResults).ToList(),
            Kanji: kanjiList
        );

        return Task.FromResult<LookupResult?>(result);
    }

    private List<TermRow> QueryTermsFromDb(IReadOnlyList<string> forms, CancellationToken ct)
    {
        var result = new List<TermRow>();
        if (forms.Count == 0) return result;

        using var conn = _dictDb.CreateConnection();
        using var cmd = conn.CreateCommand();

        var paramNames = new List<string>();
        for (int i = 0; i < forms.Count; i++)
        {
            var pName = "$p" + i;
            paramNames.Add(pName);
            cmd.Parameters.AddWithValue(pName, forms[i]);
        }

        var inClause = string.Join(", ", paramNames);
        cmd.CommandText = $"""
            SELECT t.term_id, t.dictionary_id, t.expression, t.reading, t.reading_norm,
                   t.definition_tags, t.rules, t.score, t.glossary_json, t.glossary_text,
                   t.sequence, t.term_tags, s.title AS dict_title, s.priority_order
            FROM dictionary_terms t
            JOIN dictionary_sources s ON s.dictionary_id = t.dictionary_id
            WHERE s.enabled = 1 AND (t.expression IN ({inClause}) OR t.reading_norm IN ({inClause}))
            ORDER BY s.priority_order ASC, t.score DESC;
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TermRow(
                TermId: reader.GetInt64(0),
                DictionaryId: reader.GetString(1),
                Expression: reader.GetString(2),
                Reading: reader.GetString(3),
                ReadingNorm: reader.GetString(4),
                DefinitionTags: reader.GetString(5),
                Rules: reader.GetString(6),
                Score: reader.GetDouble(7),
                GlossaryJson: reader.GetString(8),
                GlossaryText: reader.GetString(9),
                Sequence: reader.GetInt32(10),
                TermTags: reader.GetString(11),
                DictTitle: reader.GetString(12),
                PriorityOrder: reader.GetInt32(13)
            ));
        }

        return result;
    }

    private Dictionary<string, List<MetaRow>> QueryTermMetaFromDb(IReadOnlyList<string> expressions, CancellationToken ct)
    {
        var map = new Dictionary<string, List<MetaRow>>(StringComparer.Ordinal);
        if (expressions.Count == 0) return map;

        using var conn = _dictDb.CreateConnection();
        using var cmd = conn.CreateCommand();

        var paramNames = new List<string>();
        for (int i = 0; i < expressions.Count; i++)
        {
            var pName = "$m" + i;
            paramNames.Add(pName);
            cmd.Parameters.AddWithValue(pName, expressions[i]);
        }

        var inClause = string.Join(", ", paramNames);
        cmd.CommandText = $"""
            SELECT m.expression, m.reading_norm, m.mode, m.data_json, s.title AS dict_title
            FROM dictionary_term_meta m
            JOIN dictionary_sources s ON s.dictionary_id = m.dictionary_id
            WHERE s.enabled = 1 AND m.expression IN ({inClause});
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var expr = reader.GetString(0);
            var row = new MetaRow(
                Expression: expr,
                ReadingNorm: reader.IsDBNull(1) ? null : reader.GetString(1),
                Mode: reader.GetString(2),
                DataJson: reader.GetString(3),
                DictTitle: reader.GetString(4)
            );

            if (!map.TryGetValue(expr, out var list))
            {
                list = new List<MetaRow>();
                map[expr] = list;
            }
            list.Add(row);
        }

        return map;
    }

    private List<KanjiEntryViewModel> QueryKanjiFromDb(IReadOnlyList<string> kanjiChars, CancellationToken ct)
    {
        var result = new List<KanjiEntryViewModel>();
        if (kanjiChars.Count == 0) return result;

        using var conn = _dictDb.CreateConnection();
        using var cmd = conn.CreateCommand();

        var paramNames = new List<string>();
        for (int i = 0; i < kanjiChars.Count; i++)
        {
            var pName = "$k" + i;
            paramNames.Add(pName);
            cmd.Parameters.AddWithValue(pName, kanjiChars[i]);
        }

        var inClause = string.Join(", ", paramNames);
        cmd.CommandText = $"""
            SELECT character, on_readings_json, kun_readings_json,
                   primary_meanings, primary_source, kanjidic_meanings,
                   stroke_count, grade, radical, kanjivg_strokes_json
            FROM dictionary_kanji
            WHERE character IN ({inClause});
            """;

        var foundMap = new Dictionary<string, KanjiEntryViewModel>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var ch = reader.GetString(0);
            var onReadings = JsonSerializer.Deserialize<List<string>>(reader.GetString(1)) ?? [];
            var kunReadings = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? [];
            var primMeanings = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [];
            var primSource = reader.GetString(4);
            int? strokeCount = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            string? grade = reader.IsDBNull(7) ? null : reader.GetString(7);
            string? radical = reader.IsDBNull(8) ? null : reader.GetString(8);
            var strokes = reader.IsDBNull(9) ? [] : JsonSerializer.Deserialize<List<string>>(reader.GetString(9)) ?? [];

            foundMap[ch] = new KanjiEntryViewModel(
                Character: ch,
                PrimaryMeanings: primMeanings,
                PrimarySource: primSource,
                OnReadings: onReadings,
                KunReadings: kunReadings,
                StrokeCount: strokeCount,
                Grade: grade,
                Radical: radical,
                KanjiVgStrokes: strokes
            );
        }

        // Return in character order
        foreach (var ch in kanjiChars)
        {
            if (foundMap.TryGetValue(ch, out var vm))
            {
                result.Add(vm);
            }
        }

        return result;
    }

    private static (string SentenceText, IReadOnlyList<RubyUnit> SentenceRuby) ExtractSentence(
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

    private static void ParsePitches(string json, List<PitchAccentInfo> output)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pitches", out var pitchesProp) && pitchesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pitchesProp.EnumerateArray())
                {
                    int pos = p.TryGetProperty("position", out var posProp) ? posProp.GetInt32() : 0;
                    string pattern = pos switch
                    {
                        0 => "Heiban",
                        1 => "Atamadaka",
                        _ => "Nakadaka"
                    };
                    output.Add(new PitchAccentInfo("", pos, pattern));
                }
            }
        }
        catch
        {
        }
    }

    private static void ParseFreq(string dictTitle, string json, List<FrequencyBadge> output)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("frequency", out var freqProp))
            {
                if (freqProp.ValueKind == JsonValueKind.Number)
                {
                    int rank = freqProp.GetInt32();
                    output.Add(new FrequencyBadge(dictTitle, $"#{rank}", rank));
                }
                else if (freqProp.ValueKind == JsonValueKind.String)
                {
                    output.Add(new FrequencyBadge(dictTitle, freqProp.GetString()!, null));
                }
            }
        }
        catch
        {
        }
    }

    private sealed record PrefixCandidate(string Surface, int SpanLength, int EndTokenId, int TokenCount);
    private sealed record CandidateMeta(string Form, int SpanLength, int EndTokenId, IReadOnlyList<string> RuleChain, string MatchKind);
    private sealed record TermRow(
        long TermId,
        string DictionaryId,
        string Expression,
        string Reading,
        string ReadingNorm,
        string DefinitionTags,
        string Rules,
        double Score,
        string GlossaryJson,
        string GlossaryText,
        int Sequence,
        string TermTags,
        string DictTitle,
        int PriorityOrder
    );
    private sealed record MetaRow(string Expression, string? ReadingNorm, string Mode, string DataJson, string DictTitle);
    private sealed record TermCandidateGroup(TermEntryViewModel ViewModel, int SpanLength, int Tier, int PriorityOrder, double Score, int EndTokenId);
}
