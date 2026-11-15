using System.Text;
using System.Text.RegularExpressions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.DeckBuilder;

// Adapted from jiten.moe's parser (https://github.com/Sirush/Jiten, Apache-2.0): ProcessSentencesToDeck.
// See NOTICE-Jiten.md for what was adapted and what differs.

/// <summary>
/// Turns book text into a deck the way jiten.moe does: tokenise sentence by sentence, resolve every token to a
/// dictionary word, de-duplicate in first-appearance order (the first entry carries the occurrence total),
/// then compute the deck statistics and pick example sentences.
/// </summary>
public sealed partial class DeckParser(
    Func<string, IReadOnlyList<AnalyzedToken>> tokenize,
    IDeckDictionary? dictionary = null,
    Func<string, string, string>? furigana = null)
{
    [GeneratedRegex(@"[「『].{0,200}?[」』]", RegexOptions.Singleline)]
    private static partial Regex DialogueRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9぀-ゟ゠-ヿ一-龯Ａ-Ｚａ-ｚ０-９々．]")]
    private static partial Regex TokenCleanRegex();

    public DeckBuildResult Build(IReadOnlyList<string> paragraphs, DeckBuildOptions? options = null, CancellationToken ct = default)
    {
        options ??= new DeckBuildOptions();

        // 1. Sentences and tokens
        var raw = new List<(string Text, int Index, List<AnalyzedToken> Tokens)>();
        foreach (string paragraph in paragraphs)
        {
            foreach (string sentence in SentenceSplitter.Split(paragraph))
            {
                ct.ThrowIfCancellationRequested();
                var tokens = tokenize(sentence).Where(t => IsWord(t, options)).ToList();
                raw.Add((sentence, raw.Count, tokens));
            }
        }

        // 2. One dictionary round trip for every distinct (lemma, reading)
        var queries = new Dictionary<string, DictQuery>();
        foreach (var (_, _, tokens) in raw)
        {
            foreach (var t in tokens)
            {
                string k = QueryKey(t);
                if (!queries.ContainsKey(k)) queries[k] = new DictQuery(k, t.Lemma, LemmaReading(t));
            }
        }

        var entries = new Dictionary<string, DictEntry>();
        if (dictionary is not null && queries.Count > 0)
        {
            foreach (var kv in dictionary.Resolve(queries.Values.ToList())) entries[kv.Key] = kv.Value;
        }

        // 3. De-duplicate in first-appearance order, the first entry carries the occurrence total
        var words = new Dictionary<string, DeckWord>();
        var order = new List<DeckWord>();
        var sentences = new List<ParsedSentence>(raw.Count);
        var kanjiCounts = new Dictionary<char, int>();

        foreach (var (text, index, tokens) in raw)
        {
            var sentenceTokens = new List<SentenceToken>(tokens.Count);
            foreach (var t in tokens)
            {
                entries.TryGetValue(QueryKey(t), out var entry);
                string key = entry is null
                    ? $"{t.Lemma}|{LemmaReading(t)}"
                    : entry.Sequence > 0 ? $"{entry.Sequence}|{entry.Expression}" : $"{entry.Expression}|{entry.Reading}";

                if (!words.TryGetValue(key, out var word))
                {
                    string expression = entry?.Expression ?? t.Lemma;
                    string reading = entry?.Reading ?? (t.Surface == t.Lemma ? LemmaReading(t) : "");
                    word = new DeckWord
                    {
                        Key = key,
                        Word = expression,
                        ReadingKana = reading,
                        FirstOrder = order.Count,
                        IsContentWord = t.IsContentWord,
                        PartOfSpeech = t.PartOfSpeech,
                        Kind = DeckKinds.FromPartOfSpeech(t.PartOfSpeech),
                        Sequence = entry?.Sequence ?? 0,
                        Definitions = entry?.Definitions ?? "",
                        PitchPositions = entry?.PitchPositions ?? "",
                        FrequencyRank = entry?.FrequencyRank ?? 0,
                        ResolvedInDictionary = entry is not null,
                        ReadingFurigana = furigana is not null && reading.Length > 0 ? furigana(expression, reading) : ""
                    };
                    words[key] = word;
                    order.Add(word);
                }

                word.Occurrences++;
                sentenceTokens.Add(new SentenceToken(key, t.StartCharOffset, t.EndCharOffset - t.StartCharOffset, t.IsContentWord, t.Surface));

                foreach (char c in t.Surface)
                {
                    if (SentenceSplitter.IsKanji(c)) kanjiCounts[c] = kanjiCounts.GetValueOrDefault(c) + 1;
                }
            }
            sentences.Add(new ParsedSentence(text, index, sentenceTokens));
        }

        // 4. Example sentences (jiten.moe's selection passes)
        ExampleSentenceSelector.Assign(sentences, words);

        // 5. Statistics
        string fullText = string.Join("\n", paragraphs);
        string withoutDialogue = TokenCleanRegex().Replace(DialogueRegex().Replace(fullText, ""), "");
        string withoutPunctuation = TokenCleanRegex().Replace(fullText, "");
        double dialoguePercentage = withoutPunctuation.Length > 0
            ? (withoutPunctuation.Length - withoutDialogue.Length) / (double)withoutPunctuation.Length * 100.0
            : 0;

        var stats = new DeckStats(
            CharacterCount: paragraphs.Sum(p => p.Count(c => !char.IsWhiteSpace(c))),
            WordCount: order.Sum(w => w.Occurrences),
            UniqueWordCount: order.Count,
            UniqueWordUsedOnceCount: order.Count(w => w.Occurrences == 1),
            UniqueKanjiCount: kanjiCounts.Count,
            UniqueKanjiUsedOnceCount: kanjiCounts.Count(kv => kv.Value == 1),
            SentenceCount: raw.Count,
            DialoguePercentage: Math.Round(dialoguePercentage, 1),
            UnresolvedWordCount: order.Count(w => !w.ResolvedInDictionary),
            ParticleCount: order.Count(w => w.Kind == DeckKinds.Particle),
            AuxiliaryCount: order.Count(w => w.Kind == DeckKinds.Auxiliary));

        return new DeckBuildResult(stats, order);
    }

    private static string QueryKey(AnalyzedToken t) => $"{t.Lemma}\u0001{LemmaReading(t)}";

    /// <summary>
    /// The tokenizer's dictionary reading is the reading of the first morpheme, which for a merged conjugated form
    /// is only the stem. It is still a usable prefix for choosing between homographs, so it is passed on as is.
    /// </summary>
    private static string LemmaReading(AnalyzedToken t) => ToHiragana(t.DictionaryReading ?? "");

    private static bool IsWord(AnalyzedToken t, DeckBuildOptions options)
    {
        if (t.PartOfSpeech is "記号" or "補助記号" or "空白") return false;
        if (options.ContentWordsOnly && !t.IsContentWord) return false;
        foreach (char c in t.Surface)
        {
            if (SentenceSplitter.IsKana(c) && c != 'ー') return true;
            if (SentenceSplitter.IsKanji(c)) return true;
        }
        return false;
    }

    public static string ToHiragana(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            sb.Append(c >= 'ァ' && c <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }
}
