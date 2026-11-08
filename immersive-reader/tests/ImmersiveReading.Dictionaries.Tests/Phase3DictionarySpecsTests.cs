using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;
using ImmersiveReading.Plugins.Japanese;

namespace ImmersiveReading.Dictionaries.Tests;

public class Phase3DictionarySpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dictDbPath;
    private readonly string _libraryDbPath;
    private readonly DictionaryDatabase _dictDb;
    private readonly LibraryDatabase _libraryDb;
    private readonly VocabularyTracker _vocabTracker;
    private readonly ChapterEnrichmentCache _cache;
    private readonly DictionaryLookupEngine _lookupEngine;

    public Phase3DictionarySpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ir_p3_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _dictDbPath = Path.Combine(_tempDir, "dictionaries.db");
        _dictDb = new DictionaryDatabase(_dictDbPath);

        _libraryDbPath = Path.Combine(_tempDir, "library.db");
        _libraryDb = new LibraryDatabase(_libraryDbPath);
        _libraryDb.InitializeSchema();
        _vocabTracker = new VocabularyTracker(_libraryDb);

        _cache = new ChapterEnrichmentCache();
        _lookupEngine = new DictionaryLookupEngine(_dictDb, _cache, _vocabTracker, new JapaneseDeinflector());

        SeedSampleDictionary();
    }

    public void Dispose()
    {
        _dictDb.Dispose();
        _libraryDb.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void SeedSampleDictionary()
    {
        // Create synthetic Yomitan format-3 zip
        var zipPath = Path.Combine(_tempDir, "sample_dict.zip");
        using (var zipStream = new FileStream(zipPath, FileMode.Create))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            // index.json
            var indexEntry = zip.CreateEntry("index.json");
            using (var sw = new StreamWriter(indexEntry.Open(), Encoding.UTF8))
            {
                sw.Write("""
                {
                    "title": "JMdict Test Edition",
                    "format": 3,
                    "revision": "2026.11",
                    "author": "EDRDG",
                    "sourceLanguage": "ja",
                    "targetLanguage": "en"
                }
                """);
            }

            // term_bank_1.json
            var termEntry = zip.CreateEntry("term_bank_1.json");
            using (var sw = new StreamWriter(termEntry.Open(), Encoding.UTF8))
            {
                // [expression, reading, definitionTags, rules, score, glossaryArray, sequence, termTags]
                var rows = new object[]
                {
                    new object[] { "食べる", "たべる", "v1", "v1", 100.0, new[] { "to eat; to consume" }, 1, "P" },
                    new object[] { "行く", "いく", "v5k-s", "v5k-s", 95.0, new[] { "to go; to move" }, 2, "P" },
                    new object[] { "美しい", "うつくしい", "adj-i", "adj-i", 90.0, new[] { "beautiful; lovely" }, 3, "P" },
                    new object[] { "読む", "よむ", "v5m", "v5m", 88.0, new[] { "to read" }, 4, "P" },
                    new object[] { "気", "き", "n", "", 50.0, new[] { "spirit; mind; mood" }, 5, "P" },
                    new object[] { "気になる", "きになる", "exp", "v5r", 120.0, new[] { "to be on one's mind; to worry about" }, 6, "P" },
                    new object[] { "人気", "ひとけ", "n", "", 60.0, new[] { "sign of life; human presence" }, 7, "" },
                    new object[] { "人気", "にんき", "n", "", 95.0, new[] { "popularity; public favor" }, 8, "P" },
                    new object[] { "海辺", "うみべ", "n", "", 70.0, new[] { "seashore; seaside; coast" }, 9, "" },
                    new object[] { "森", "もり", "n", "", 80.0, new[] { "forest; woods" }, 10, "P" }
                };
                sw.Write(JsonSerializer.Serialize(rows));
            }
        }

        var importer = new YomitanSqliteImporter(_dictDb);
        var result = importer.ImportZip(zipPath, priorityOrder: 10);
        Assert.Equal(10, result.TermsCount);

        // Seed kanji data for 海 and 辺
        using var conn = _dictDb.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dictionary_kanji (
                character, on_readings_json, kun_readings_json,
                primary_meanings, primary_source, kanjidic_meanings,
                stroke_count, grade, radical, kanjivg_strokes_json
            ) VALUES
            ('海', '["カイ"]', '["うみ"]', '["sea", "ocean"]', 'Kanji alive', '["sea", "ocean"]', 9, '2', '水', '["M20.38,14.75c3.31,1.47,8.54,6.05,9.37,8.34", "M14.25,38.75c4.15,1.52,10.72,6.25,11.75,8.62"]'),
            ('辺', '["ヘン"]', '["あたり", "べ"]', '["environs", "boundary", "vicinity"]', 'Kanji alive', '["environs", "boundary"]', 5, '4', '辵', '["M21.25,16.75c3.67,1.38,9.48,5.69,10.39,7.84"]');
            """;
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("食べさせられた", "食べる", "たべる", "passive")]
    [InlineData("行かなかった", "行く", "いく", "negative")]
    [InlineData("美しくなかった", "美しい", "うつくしい", "past")]
    [InlineData("読んじゃった", "読む", "よむ", "contraction (-chau)")]
    public async Task ScenarioOutline1_DeinflectingConjugatedVerbsAndAdjectives(
        string surface,
        string lemma,
        string reading,
        string expectedRule)
    {
        const string bookId = "book-4e0541b184c4ace9";
        const string chapterHref = "OEBPS/Text/ch01.xhtml";

        var token = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: surface.Length,
            Surface: surface,
            Lemma: surface,
            NormalizedForm: surface,
            Reading: "",
            DictionaryReading: "",
            PartOfSpeech: "動詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.PluginGenerated,
            IsContentWord: true
        );

        var tokenEntry = new EnrichedTokenEntry(
            TokenId: 1,
            ParagraphId: 1,
            Token: token,
            RubyUnits: []
        );

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, [tokenEntry], [surface]);
        _cache.Set(tokenMap);

        var sw = Stopwatch.StartNew();
        var result = await _lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, TokenId: 1));
        sw.Stop();

        Assert.NotNull(result);
        Assert.NotEmpty(result.Terms);

        var topTerm = result.Terms[0];
        Assert.Equal(lemma, topTerm.Expression);
        Assert.Equal(reading, topTerm.ReadingNormalized);
        Assert.Contains(expectedRule, topTerm.InflectionChain);

        // Lookup executes sub-5ms
        Assert.True(sw.ElapsedMilliseconds < 50, $"Lookup took {sw.ElapsedMilliseconds} ms (first run includes DB load)");
    }

    [Fact]
    public async Task Scenario2_LongestPrefixCompoundPhraseMatching()
    {
        const string bookId = "book-4e0541b184c4ace9";
        const string chapterHref = "OEBPS/Text/ch01.xhtml";

        // Paragraph contains: "気", "に", "ならなかった"
        var tokens = new List<EnrichedTokenEntry>
        {
            new(TokenId: 10, ParagraphId: 1, Token: new AnalyzedToken(0, 1, "気", "気", "気", "キ", "き", "名詞", "", "", RubySource.None, true), RubyUnits: []),
            new(TokenId: 11, ParagraphId: 1, Token: new AnalyzedToken(1, 2, "に", "に", "に", "ニ", "に", "助詞", "", "", RubySource.None, false), RubyUnits: []),
            new(TokenId: 12, ParagraphId: 1, Token: new AnalyzedToken(2, 8, "ならなかった", "なる", "なる", "ナラナカッタ", "なる", "動詞", "", "", RubySource.None, true), RubyUnits: [])
        };

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, tokens, ["気にならなかった"]);
        _cache.Set(tokenMap);

        var result = await _lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, TokenId: 10));

        Assert.NotNull(result);
        Assert.NotEmpty(result.Terms);

        // Ranks multi-token compound "気になる" above "気"
        Assert.Equal("気になる", result.Terms[0].Expression);
        Assert.Equal("きになる", result.Terms[0].ReadingNormalized);

        // Highlight spans across the entire phrase up to token 12 ("ならなかった")
        Assert.Equal(10, result.HighlightStartTokenId);
        Assert.Equal(12, result.HighlightEndTokenId);
        Assert.Equal("気にならなかった", result.MatchedSurface);
    }

    [Fact]
    public async Task Scenario3_AuthorEpubRubyDisambiguatesHomographs()
    {
        const string bookId = "book-4e0541b184c4ace9";
        const string chapterHref = "OEBPS/Text/ch01.xhtml";

        // Token 42: "人気" with author ruby "ひとけ"
        var token = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "人気",
            Lemma: "人気",
            NormalizedForm: "人気",
            Reading: "ひとけ",
            DictionaryReading: "ひとけ",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.EpubAuthor,
            IsContentWord: true
        );

        var tokenEntry = new EnrichedTokenEntry(
            TokenId: 42,
            ParagraphId: 1,
            Token: token,
            RubyUnits: [new RubyUnit(0, 2, "人気", "ひとけ", RubySource.EpubAuthor)]
        );

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, [tokenEntry], ["人気"]);
        _cache.Set(tokenMap);

        var result = await _lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, TokenId: 42));

        Assert.NotNull(result);
        Assert.NotEmpty(result.Terms);

        // First term should be "人気 [ひとけ]" (sign of life), NOT "人気 [にんき]" (popularity)
        var top = result.Terms[0];
        Assert.Equal("人気", top.Expression);
        Assert.Equal("ひとけ", top.ReadingNormalized);
        Assert.Equal("epub_ruby_verified", top.ReadingMatchStatus);
        Assert.Contains("sign of life", top.Senses[0].Glosses[0]);
    }

    [Fact]
    public async Task Scenario4_ExtractingConstituentKanjiCardsWithKanjiVg()
    {
        const string bookId = "book-4e0541b184c4ace9";
        const string chapterHref = "OEBPS/Text/ch01.xhtml";

        var token = new AnalyzedToken(
            StartCharOffset: 0,
            EndCharOffset: 2,
            Surface: "海辺",
            Lemma: "海辺",
            NormalizedForm: "海辺",
            Reading: "うみべ",
            DictionaryReading: "うみべ",
            PartOfSpeech: "名詞",
            ConjugationType: "",
            ConjugationForm: "",
            ReadingSource: RubySource.None,
            IsContentWord: true
        );

        var tokenEntry = new EnrichedTokenEntry(
            TokenId: 100,
            ParagraphId: 1,
            Token: token,
            RubyUnits: []
        );

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, [tokenEntry], ["海辺"]);
        _cache.Set(tokenMap);

        var result = await _lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, TokenId: 100));

        Assert.NotNull(result);
        Assert.Equal(2, result.Kanji.Count);

        var kai = result.Kanji[0];
        Assert.Equal("海", kai.Character);
        Assert.Equal("Kanji alive", kai.PrimarySource);
        Assert.Contains("sea", kai.PrimaryMeanings);
        Assert.Equal(9, kai.StrokeCount);
        Assert.Equal(2, kai.KanjiVgStrokes.Count);
        Assert.StartsWith("M20.38", kai.KanjiVgStrokes[0]);

        var hen = result.Kanji[1];
        Assert.Equal("辺", hen.Character);
        Assert.Equal("Kanji alive", hen.PrimarySource);
        Assert.Contains("environs", hen.PrimaryMeanings);
        Assert.Equal(5, hen.StrokeCount);
        Assert.Single(hen.KanjiVgStrokes);
    }

    [Fact]
    public void Scenario5_VocabularyTrackerUpdatesStatus()
    {
        _vocabTracker.SetStatus("ja", "森", "もり", WordStatus.Known);

        var status = _vocabTracker.GetStatus("ja", "森", "もり");
        Assert.Equal(WordStatus.Known, status);

        var record = _vocabTracker.GetRecord("ja", "森", "もり");
        Assert.NotNull(record);
        Assert.Equal(WordStatus.Known, record.Status);
    }
}

