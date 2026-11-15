using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Plugins.DeckBuilder;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>
/// Deck Builder (jiten.moe style, NOTICE-Jiten.md). Pure logic is tested with a fake tokenizer and dictionary so the
/// tests need neither MeCab nor imported dictionaries; the SQL dictionary is tested against a real SQLite file.
/// </summary>
public class DeckBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ir-deck-" + Guid.NewGuid().ToString("N"));

    public DeckBuilderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    // ---- fakes -----------------------------------------------------------------------------------

    /// <summary>Tokenizes by a fixed vocabulary: every occurrence of a known word becomes a token.</summary>
    private static Func<string, IReadOnlyList<AnalyzedToken>> FakeTokenizer(params (string Surface, string Lemma, string Reading, bool Content)[] vocab)
    {
        return sentence =>
        {
            var tokens = new List<(int Start, AnalyzedToken Token)>();
            foreach (var (surface, lemma, reading, content) in vocab)
            {
                int from = 0;
                while ((from = sentence.IndexOf(surface, from, StringComparison.Ordinal)) >= 0)
                {
                    tokens.Add((from, new AnalyzedToken(from, from + surface.Length, surface, lemma, lemma, reading, reading,
                        content ? "名詞" : "助詞", "", "", RubySource.None, content)));
                    from += surface.Length;
                }
            }
            return tokens.OrderBy(t => t.Start).Select(t => t.Token).ToList();
        };
    }

    private sealed class FakeDictionary(Dictionary<string, DictEntry> byLemma) : IDeckDictionary
    {
        public IReadOnlyDictionary<string, DictEntry> Resolve(IReadOnlyCollection<DictQuery> queries)
            => queries.Where(q => byLemma.ContainsKey(q.Lemma)).ToDictionary(q => q.Key, q => byLemma[q.Lemma]);
    }

    // ---- sentence splitting ----------------------------------------------------------------------

    [Fact]
    public void Splitter_KeepsQuotedDialogueWhole_AndSplitsAtClosingBracket()
    {
        var parts = SentenceSplitter.Split("「はい。そうです。」と彼は言った。次の文。").ToList();
        Assert.Equal(["「はい。そうです。」", "と彼は言った。", "次の文。"], parts);
    }

    [Fact]
    public void Splitter_AttachesRunsOfMarksAndDropsPunctuationOnlyFragments()
    {
        var parts = SentenceSplitter.Split("本当？！　……。").ToList();
        Assert.Equal(["本当？！"], parts);
    }

    // ---- text extraction -------------------------------------------------------------------------

    [Fact]
    public void Extractor_DropsRubyReadings_AndSplitsBlockElements()
    {
        const string xhtml = "<html><body><h1>題</h1><p>漢<ruby>字<rt>じ</rt></ruby>を読む。<br/>次の行。</p><script>x()</script></body></html>";
        var paragraphs = BookTextExtractor.ExtractFromMarkup(xhtml);
        Assert.Equal(["題", "漢字を読む。", "次の行。"], paragraphs);
    }

    // ---- parser: first-appearance order, occurrence totals, stats ----------------------------------

    [Fact]
    public void Parser_DeduplicatesInFirstAppearanceOrder_AndCountsOccurrences()
    {
        var tokenizer = FakeTokenizer(("猫", "猫", "ねこ", true), ("犬", "犬", "いぬ", true), ("は", "は", "は", false));
        var parser = new DeckParser(tokenizer);

        var result = parser.Build(["猫は犬。犬は猫。猫は猫。"]);

        Assert.Equal(["猫", "は", "犬"], result.Words.Select(w => w.Word).ToArray());
        Assert.Equal(4, result.Words[0].Occurrences);
        Assert.Equal(3, result.Words[1].Occurrences);
        Assert.Equal(2, result.Words[2].Occurrences);
        Assert.Equal(9, result.Stats.WordCount);
        Assert.Equal(3, result.Stats.UniqueWordCount);
        Assert.Equal(0, result.Stats.UniqueWordUsedOnceCount);
        Assert.Equal(3, result.Stats.SentenceCount);
        Assert.Equal(2, result.Stats.UniqueKanjiCount);
        Assert.Equal(3, result.Stats.UnresolvedWordCount);
    }

    [Fact]
    public void Parser_MergesSurfaceVariantsOfOneDictionaryEntry()
    {
        // Two inflected forms of the same lemma resolve to the same sequence: one deck word, summed occurrences.
        var tokenizer = FakeTokenizer(("食べた", "食べる", "たべ", true), ("食べない", "食べる", "たべ", true));
        var entry = new DictEntry("食べる", "たべる", 1358280, "to eat", "2", 321);
        var parser = new DeckParser(tokenizer, new FakeDictionary(new() { ["食べる"] = entry }));

        var result = parser.Build(["食べた。食べない。"]);

        var word = Assert.Single(result.Words);
        Assert.Equal(2, word.Occurrences);
        Assert.Equal(1358280, word.Sequence);
        Assert.Equal("たべる", word.ReadingKana);
        Assert.Equal(321, word.FrequencyRank);
        Assert.True(word.ResolvedInDictionary);
    }

    [Fact]
    public void Parser_ContentWordsOnly_SkipsFunctionWords_AndSymbolsAreNeverWords()
    {
        var tokenizer = FakeTokenizer(("猫", "猫", "ねこ", true), ("は", "は", "は", false));
        var result = new DeckParser(tokenizer).Build(["猫は猫。"], new DeckBuildOptions(ContentWordsOnly: true));
        Assert.Equal(["猫"], result.Words.Select(w => w.Word).ToArray());
    }

    [Fact]
    public void Parser_ComputesDialoguePercentage()
    {
        var result = new DeckParser(FakeTokenizer()).Build(["「ああ」いいいい"]);
        // 2 dialogue chars of 6 letters in total.
        Assert.Equal(33.3, result.Stats.DialoguePercentage);
    }

    // ---- example sentences (jiten.moe passes) ------------------------------------------------------

    [Fact]
    public void ExampleSelector_ChoosesQualifyingSentence_MarksTarget_AndUsesEachSentenceOnce()
    {
        var tokenizer = FakeTokenizer(("先生", "先生", "せんせい", true), ("学校", "学校", "がっこう", true), ("宿題", "宿題", "しゅくだい", true));
        var parser = new DeckParser(tokenizer);

        // Too short to qualify (fewer than 10 characters, fewer than 6 distinct characters)
        const string tooShort = "先生。";
        // 14 characters, >= 2 content words, distinct chars >= 6: qualifies for pass 2 (10-45)
        const string good = "先生は学校で宿題を出した。";

        var result = parser.Build([tooShort, good]);

        var teacher = result.Words.Single(w => w.Word == "先生");
        var school = result.Words.Single(w => w.Word == "学校");
        Assert.Equal("**先生**は学校で宿題を出した。", teacher.ExampleSentence);
        Assert.Equal("先生は**学校**で宿題を出した。", school.ExampleSentence);
    }

    [Fact]
    public void ExampleSelector_BalancesBracketsAndShiftsTargetPosition()
    {
        var (text, shift) = ExampleSentenceSelector.Tidy("）彼は「行く");
        Assert.Equal("彼は「行く」", text);
        Assert.Equal(-1, shift);
    }

    // ---- exporter ---------------------------------------------------------------------------------

    private static DeckWord W(string word, int first, int occ, int freq = 0, string reading = "あ", string example = "", long seq = 0, string defs = "")
        => new() { Key = word + first, Word = word, FirstOrder = first, Occurrences = occ, FrequencyRank = freq, ReadingKana = reading, ExampleSentence = example, Sequence = seq, Definitions = defs };

    [Fact]
    public void Exporter_OrdersChronologicalDeckAndGlobalFrequency()
    {
        var words = new List<DeckWord> { W("a", 0, 1, freq: 500), W("b", 1, 9, freq: 0), W("c", 2, 5, freq: 20) };

        Assert.Equal(["a", "b", "c"], DeckExporter.Select(words, new DeckExportOptions(Order: DeckOrder.Chronological)).Select(w => w.Word));
        Assert.Equal(["b", "c", "a"], DeckExporter.Select(words, new DeckExportOptions(Order: DeckOrder.DeckFrequency)).Select(w => w.Word));
        // unknown rank (0) sorts last
        Assert.Equal(["c", "a", "b"], DeckExporter.Select(words, new DeckExportOptions(Order: DeckOrder.GlobalFrequency)).Select(w => w.Word));
    }

    [Fact]
    public void Exporter_SelectionTypes()
    {
        var words = new List<DeckWord> { W("a", 0, 50), W("b", 1, 30), W("c", 2, 15), W("d", 3, 5) };

        Assert.Equal(2, DeckExporter.Select(words, new DeckExportOptions(Selection: DeckDownloadType.TopN, SelectionValue: 2)).Count);
        Assert.Equal(["a", "b", "c"], DeckExporter.Select(words, new DeckExportOptions(Selection: DeckDownloadType.MinOccurrences, SelectionValue: 10)).Select(w => w.Word));
        // 80 % of 100 occurrences: a (50) + b (30) reaches it exactly
        Assert.Equal(["a", "b"], DeckExporter.Select(words, new DeckExportOptions(Selection: DeckDownloadType.TargetCoverage, SelectionValue: 80)).Select(w => w.Word));
    }

    [Fact]
    public void Exporter_ExcludeKana_RemovesKanaOnlyWords()
    {
        var words = new List<DeckWord> { W("です", 0, 3), W("猫", 1, 1), W("コーヒー", 2, 1) };
        Assert.Equal(["猫"], DeckExporter.Select(words, new DeckExportOptions(ExcludeKana: true)).Select(w => w.Word));
    }

    [Fact]
    public void Exporter_Csv_HasJitenColumnsPlusKindColumns_AndEveryRowParsesToTwelveFields()
    {
        var words = new List<DeckWord>
        {
            W("猫", 0, 3, freq: 10, reading: "ねこ", example: "**猫**が\"鳴く\"。", seq: 1467640, defs: "cat,\nfeline"),
            W("犬", 1, 1, reading: "いぬ")
        };

        string csv = DeckExporter.Render(words, new DeckExportOptions());
        var lines = csv.TrimEnd('\n').Split('\n');

        Assert.Equal(DeckExporter.CsvHeader, lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 words: a newline inside a field must not split the record
        foreach (string line in lines) Assert.Equal(12, ParseCsvLine(line).Count);
        var cat = ParseCsvLine(lines[1]);
        Assert.Equal(["猫", "", "ねこ", "3", "10", "", "cat, feline", "**猫**が\"鳴く\"。", "1467640", "vocabulary", "", ""], cat);
    }

    [Fact]
    public void Exporter_Csv_ExcludeExampleSentences_BlanksColumn()
    {
        var words = new List<DeckWord> { W("猫", 0, 1, example: "**猫**だ。") };
        var row = ParseCsvLine(DeckExporter.Render(words, new DeckExportOptions(ExcludeExampleSentences: true)).Split('\n')[1]);
        Assert.Equal("", row[7]);
    }

    [Fact]
    public void Exporter_TxtRepeated_WritesWordOncePerOccurrence()
    {
        var words = new List<DeckWord> { W("猫", 0, 3), W("犬", 1, 1) };
        Assert.Equal("猫\n猫\n猫\n犬\n", DeckExporter.Render(words, new DeckExportOptions(Format: DeckFormat.TxtRepeated)));
        Assert.Equal("猫\n犬\n", DeckExporter.Render(words, new DeckExportOptions(Format: DeckFormat.Txt)));
    }

    /// <summary>Minimal RFC-4180 line parser mirroring what Python's csv.DictReader does for the pipeline.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }


    // ---- function words (particles / auxiliaries) ---------------------------------------------------

    [Fact]
    public void RealMeCab_MarksParticlesAndAuxiliariesAsFunctionWords_NotVocabulary()
    {
        var ja = new ImmersiveReading.Plugins.Japanese.JapaneseLanguagePlugin();
        using var tokenizer = ja.CreateTokenizer();
        var result = new DeckParser(s => tokenizer.TokenizeParagraph(s.AsSpan(), [])).Build(["これは本です。猫が好きだ。"]);

        Assert.Equal(DeckKinds.Particle, result.Words.Single(w => w.Word == "は").Kind);
        Assert.Equal(DeckKinds.Particle, result.Words.Single(w => w.Word == "が").Kind);
        Assert.Equal("助詞", result.Words.Single(w => w.Word == "が").PartOfSpeech);
        Assert.Equal(DeckKinds.Auxiliary, result.Words.Single(w => w.Word == "です").Kind);
        Assert.Equal(DeckKinds.Vocabulary, result.Words.Single(w => w.Word == "本").Kind);
        Assert.True(result.Stats.ParticleCount >= 2);
        Assert.True(result.Stats.AuxiliaryCount >= 1);
    }

    [Fact]
    public void Exporter_WritesKindAndUsageNote_AndCanDropFunctionWords()
    {
        var particle = new DeckWord { Key = "に", Word = "に", FirstOrder = 0, Occurrences = 7, Kind = DeckKinds.Particle, PartOfSpeech = "助詞", UsageNote = "destination: where to" };
        var noun = W("猫", 1, 2);
        var words = new List<DeckWord> { particle, noun };

        var rows = DeckExporter.Render(words, new DeckExportOptions()).TrimEnd((char)10).Split((char)10).Skip(1).Select(ParseCsvLine).ToList();
        Assert.Equal(["particle", "助詞", "destination: where to"], rows[0].Skip(9).ToArray());
        Assert.Equal("vocabulary", rows[1][9]);

        var kept = DeckExporter.Select(words, new DeckExportOptions(ExcludeFunctionWords: true));
        Assert.Equal(["猫"], kept.Select(w => w.Word).ToArray());
    }

    // ---- SQL dictionary ---------------------------------------------------------------------------

    [Fact]
    public void SqliteDictionary_ResolvesSequenceGlossFrequencyAndPitch_PreferringMatchingReading()
    {
        using var db = new DictionaryDatabase(Path.Combine(_tempDir, "dict.db"));
        using (var conn = db.CreateConnection())
        {
            Exec(conn, "INSERT INTO dictionary_sources(dictionary_id,title,revision,priority_order,enabled,imported_at_utc) VALUES('jm','JMdict','1',1,1,'2026-01-01')");
            Exec(conn, "INSERT INTO dictionary_sources(dictionary_id,title,revision,priority_order,enabled,imported_at_utc) VALUES('off','Disabled','1',0,0,'2026-01-01')");
            // Two homographs of 生: only the reading decides which one a token "いき" (stem of 生きる) hits.
            Term(conn, "jm", "生きる", "いきる", "いきる", 5, "to live\nto exist", 1, 1);
            Term(conn, "jm", "生きる", "うまれる", "うまれる", 9, "to be born", 2, 1);
            // A disabled dictionary must never contribute
            Term(conn, "off", "生きる", "いきる", "いきる", 99, "WRONG", 3, 0);
            Exec(conn, "INSERT INTO dictionary_term_meta(dictionary_id,expression,reading_norm,mode,data_json) VALUES('jm','生きる','いきる','freq','{\"frequency\":1234}')");
            Exec(conn, "INSERT INTO dictionary_term_meta(dictionary_id,expression,reading_norm,mode,data_json) VALUES('jm','生きる','いきる','pitch','{\"pitches\":[{\"position\":2}]}')");
        }

        var dict = new SqliteDeckDictionary(db);
        var result = dict.Resolve([new DictQuery("k1", "生きる", "いき"), new DictQuery("k2", "存在しない", "")]);

        var entry = Assert.Single(result).Value;
        Assert.Equal("生きる", entry.Expression);
        Assert.Equal("いきる", entry.Reading);
        Assert.Equal(1, entry.Sequence);
        Assert.Equal("to live, to exist", entry.Definitions);
        Assert.Equal(1234, entry.FrequencyRank);
        Assert.Equal("2", entry.PitchPositions);
    }

    [Fact]
    public void RealMeCab_ConjugatedVerbsResolveToTheirDictionaryForm_AndSymbolsAreSkipped()
    {
        using var db = new DictionaryDatabase(Path.Combine(_tempDir, "dict2.db"));
        using (var conn = db.CreateConnection())
        {
            Exec(conn, "INSERT INTO dictionary_sources(dictionary_id,title,revision,priority_order,enabled,imported_at_utc) VALUES('jm','JMdict','1',1,1,'2026-01-01')");
            Term(conn, "jm", "読む", "よむ", "よむ", 5, "to read", 1, 1);
            Term(conn, "jm", "本", "ほん", "ほん", 5, "book", 2, 1);
        }

        var ja = new ImmersiveReading.Plugins.Japanese.JapaneseLanguagePlugin();
        using var tokenizer = ja.CreateTokenizer();
        var parser = new DeckParser(
            s => tokenizer.TokenizeParagraph(s.AsSpan(), []),
            new SqliteDeckDictionary(db),
            (word, reading) => word);

        var result = parser.Build(["彼は本を読んだ。本を読む！"]);

        var read = result.Words.Single(w => w.Word == "読む");
        Assert.Equal(2, read.Occurrences);
        Assert.Equal("よむ", read.ReadingKana);
        Assert.Equal(1, read.Sequence);
        Assert.Equal(2, result.Words.Single(w => w.Word == "本").Occurrences);
        // punctuation never becomes a deck word
        Assert.DoesNotContain(result.Words, w => w.Word is "。" or "！");
    }

    private static void Term(Microsoft.Data.Sqlite.SqliteConnection c, string dict, string expr, string reading, string norm, double score, string gloss, long seq, int _)
        => Exec(c, $"INSERT INTO dictionary_terms(dictionary_id,expression,reading,reading_norm,definition_tags,rules,score,glossary_json,glossary_text,sequence,term_tags,bank_name,bank_row) VALUES('{dict}','{expr}','{reading}','{norm}','','',{score},'[]','{gloss.Replace("\n", "' || char(10) || '")}',{seq},'','b',0)");

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
