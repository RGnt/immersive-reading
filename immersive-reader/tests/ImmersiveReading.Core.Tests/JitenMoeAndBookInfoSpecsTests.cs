using System.Text.Json;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.Core.Tests;

public sealed class JitenMoeAndBookInfoSpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public JitenMoeAndBookInfoSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-jiten-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "library.db");
        string coversDir = Path.Combine(_tempDir, "covers");
        _db = new LibraryDatabase(dbPath, coversDir);
        _db.InitializeSchema();
        _manager = new LibraryManager(_db, Path.Combine(_tempDir, "output"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort on Windows
        }
    }

    [Fact]
    public void JitenFields_CanBeAppliedToBookAndInherited()
    {
        // 1. Create a Series folder with jiten_moe_url set
        var series = _manager.SaveFolder(
            null,
            null,
            "86 - Eighty Six",
            "series",
            customFields: new List<CustomFieldValueInput>
            {
                new("jiten_moe_url", "https://jiten.moe/decks/media/54961", null)
            });

        // 2. Insert a book in that series
        InsertTestBook("book-86-v1", series.FolderId, "86 - Eighty Six Vol 1");

        // 3. Verify book inherited the series jiten_moe_url
        var detail1 = _manager.GetBookDetail("book-86-v1");
        Assert.NotNull(detail1);
        var jitenUrlField = detail1.EffectiveCustomFields.FirstOrDefault(cf => cf.FieldKey == "jiten_moe_url");
        Assert.NotNull(jitenUrlField);
        Assert.Equal("https://jiten.moe/decks/media/54961", jitenUrlField.EffectiveValueText);
        Assert.True(jitenUrlField.IsInherited);

        // 4. Directly override jiten_moe_url for Volume 1 subdeck and add jiten_difficulty
        _manager.SetCustomFieldValue("book", "book-86-v1", "jiten_moe_url", "https://jiten.moe/decks/media/54963", null);
        _manager.SetCustomFieldValue("book", "book-86-v1", "jiten_difficulty", "3.88", 3.88);

        // 5. Verify direct fields take precedence
        var detail2 = _manager.GetBookDetail("book-86-v1");
        Assert.NotNull(detail2);

        var directUrlField = detail2.EffectiveCustomFields.FirstOrDefault(cf => cf.FieldKey == "jiten_moe_url");
        Assert.NotNull(directUrlField);
        Assert.Equal("https://jiten.moe/decks/media/54963", directUrlField.EffectiveValueText);
        Assert.False(directUrlField.IsInherited);

        var diffField = detail2.EffectiveCustomFields.FirstOrDefault(cf => cf.FieldKey == "jiten_difficulty");
        Assert.NotNull(diffField);
        Assert.Equal("3.88", diffField.EffectiveValueText);
        Assert.Equal(3.88, diffField.EffectiveValueNumber);
    }

    [Fact]
    public void JitenApiResponse_PayloadStructureIsValid()
    {
        // Validate that our JSON contract parses mock Jiten.moe API payloads accurately
        const string mockJson = """
        {
          "data": [
            {
              "deckId": 54961,
              "originalTitle": "86―エイティシックス―",
              "romajiTitle": "86: Eighty Six",
              "englishTitle": "86―EIGHTY-SIX",
              "description": "A War Without Casualties...",
              "characterCount": 1603952,
              "wordCount": 707919,
              "uniqueWordCount": 14200,
              "uniqueKanjiCount": 2100,
              "difficulty": 3,
              "difficultyRaw": 3.93,
              "coverName": "https://cdn.jiten.moe/54961/cover.jpg",
              "mediaType": 4,
              "childrenDeckCount": 12,
              "genres": [1, 6, 13],
              "tags": [
                { "tagId": 142, "name": "Magic", "percentage": 88 },
                { "tagId": 8, "name": "Military", "percentage": 95 }
              ],
              "links": [
                { "linkId": 100, "linkType": 4, "url": "https://anilist.co/manga/54961" },
                { "linkId": 101, "linkType": 10, "url": "https://bookmeter.com/books/54961" }
              ]
            }
          ],
          "totalItems": 1
        }
        """;

        using var doc = JsonDocument.Parse(mockJson);
        Assert.True(doc.RootElement.TryGetProperty("data", out var dataArr));
        var first = dataArr.EnumerateArray().First();

        var summary = JitenMoeService.ParseDeck(first);

        Assert.Equal(54961, summary.DeckId);
        Assert.Equal("86―エイティシックス―", summary.OriginalTitle);
        Assert.Equal("86: Eighty Six", summary.RomajiTitle);
        Assert.Equal("Novel", summary.MediaTypeName);
        Assert.Equal(3.93, summary.Difficulty);
        Assert.Equal(1603952, summary.CharacterCount);
        Assert.Equal(14200, summary.UniqueWordCount);
        Assert.Equal(2100, summary.UniqueKanjiCount);
        Assert.Equal(3, summary.Genres.Count);
        Assert.Contains("Action", summary.Genres);
        Assert.Contains("Fantasy", summary.Genres);
        Assert.Contains("Sci-Fi", summary.Genres);
        Assert.Equal(2, summary.Tags.Count);
        Assert.Equal("Magic", summary.Tags[0].Name);
        Assert.Equal(88, summary.Tags[0].Percentage);
        Assert.Equal(2, summary.Links.Count);
        Assert.Equal("AniList", summary.Links[0].TypeName);
        Assert.Equal("https://anilist.co/manga/54961", summary.Links[0].Url);
        Assert.Equal("Bookmeter", summary.Links[1].TypeName);
    }

    [Fact]
    public void JitenDifficultyAndStats_ParsingIsValid()
    {
        const string diffJson = """
        {
          "difficulty": 1.51,
          "peak": 2.72,
          "distinctVoterCount": 42,
          "deciles": {
            "10": 0.92,
            "50": 1.51,
            "90": 3.41
          },
          "progression": [
            { "segment": 0, "difficulty": 1.35, "peak": 1.76 },
            { "segment": 1, "difficulty": 1.62, "peak": 2.10 }
          ]
        }
        """;

        using var diffDoc = JsonDocument.Parse(diffJson);
        var diffResult = JitenMoeService.ParseDifficulty(54738, diffDoc.RootElement);
        Assert.Equal(54738, diffResult.DeckId);
        Assert.Equal(1.51, diffResult.Difficulty);
        Assert.Equal(2.72, diffResult.Peak);
        Assert.Equal(42, diffResult.DistinctVoterCount);
        Assert.Equal(3, diffResult.Deciles.Count);
        Assert.Equal(0.92, diffResult.Deciles["10"]);
        Assert.Equal(1.51, diffResult.Deciles["50"]);
        Assert.Equal(3.41, diffResult.Deciles["90"]);
        Assert.Equal(2, diffResult.Progression.Count);
        Assert.Equal(1.35, diffResult.Progression[0].Difficulty);

        const string statsJson = """
        {
          "deckId": 54738,
          "totalUniqueWords": 3645,
          "computedAt": "2026-10-01T21:05:57Z",
          "rSquared": 0.9588,
          "milestones": {
            "80%": 521,
            "90%": 1147,
            "95%": 1914,
            "99%": 3285
          }
        }
        """;

        using var statsDoc = JsonDocument.Parse(statsJson);
        var statsResult = JitenMoeService.ParseStats(54738, statsDoc.RootElement);
        Assert.Equal(54738, statsResult.DeckId);
        Assert.Equal(3645, statsResult.TotalUniqueWords);
        Assert.Equal(0.9588, statsResult.RSquared);
        Assert.Equal(4, statsResult.Milestones.Count);
        Assert.Equal(521, statsResult.Milestones["80%"]);
        Assert.Equal(1147, statsResult.Milestones["90%"]);
    }

    [Fact]
    public void JitenVocabularyPreview_ParsingIsValid()
    {
        const string vocabJson = """
        [
          {
            "wordId": 1524230,
            "readingIndex": 0,
            "reading": "魔法",
            "readingFurigana": "魔[ま]法[ほう]",
            "mainDefinition": "magic",
            "frequencyRank": 1990,
            "occurrences": 157
          },
          {
            "wordId": 1519840,
            "readingIndex": 0,
            "reading": "冒険者",
            "readingFurigana": "冒[ぼう]険[けん]者[しゃ]",
            "mainDefinition": "adventurer",
            "frequencyRank": 16552,
            "occurrences": 148
          }
        ]
        """;

        using var vocabDoc = JsonDocument.Parse(vocabJson);
        var preview = JitenMoeService.ParseVocabularyPreview(vocabDoc.RootElement);
        Assert.Equal(2, preview.Count);
        Assert.Equal(1524230, preview[0].WordId);
        Assert.Equal("魔法", preview[0].Reading);
        Assert.Equal("魔[ま]法[ほう]", preview[0].ReadingFurigana);
        Assert.Equal("magic", preview[0].MainDefinition);
        Assert.Equal(1990, preview[0].FrequencyRank);
        Assert.Equal(157, preview[0].Occurrences);

        Assert.Equal("冒険者", preview[1].Reading);
        Assert.Equal("adventurer", preview[1].MainDefinition);
    }

    [Fact]
    public void JitenFindMatchingSubDeck_FindsVolumeCorrectly()
    {
        var subDecks = new List<JitenDeckSummary>
        {
            new(54738, "Volume 1", null, null, null, 4, "Novel", null, 80000, 35000, 3600, 1100, 1.5, 1.51, 0, [], [], [], "https://jiten.moe/decks/media/54738"),
            new(54739, "Volume 2", null, null, null, 4, "Novel", null, 90000, 38000, 3900, 1200, 1.7, 1.72, 0, [], [], [], "https://jiten.moe/decks/media/54739"),
            new(54740, "Volume 3", null, null, null, 4, "Novel", null, 100000, 42000, 4200, 1300, 1.9, 1.88, 0, [], [], [], "https://jiten.moe/decks/media/54740")
        };

        var matched1 = JitenMoeService.FindMatchingSubDeck(subDecks, 1.0, "くま クマ 熊 ベアー 1");
        Assert.NotNull(matched1);
        Assert.Equal(54738, matched1.DeckId);
        Assert.Equal("Volume 1", matched1.OriginalTitle);

        var matched2 = JitenMoeService.FindMatchingSubDeck(subDecks, 2.0, "くま クマ 熊 ベアー 2");
        Assert.NotNull(matched2);
        Assert.Equal(54739, matched2.DeckId);

        var matched3 = JitenMoeService.FindMatchingSubDeck(subDecks, 3.0, "くま クマ 熊 ベアー 3");
        Assert.NotNull(matched3);
        Assert.Equal(54740, matched3.DeckId);
    }

    [Fact]
    public void JitenApiKey_SettingAndResolving_Works()
    {
        JitenMoeService.SetApiKey("test-key-123");
        Assert.Equal("test-key-123", JitenMoeService.ResolveApiKey());

        JitenMoeService.SetApiKey(null);
        // Reset
    }

    private void InsertTestBook(string bookId, string? folderId, string title)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        string now = DateTime.UtcNow.ToString("O");
        cmd.CommandText = """
            INSERT INTO books (
                book_id, epub_sha256, folder_id, title, sort_title,
                series_name, series_index, custom_sort_order, authors, author_sort,
                isbn, publisher, imprint, published_date, language, description,
                is_metadata_edited, folder_path, epub_file_name, file_size_bytes,
                cover_cache_path, chapter_count, total_char_count, stats_computed,
                writing_mode, added_at_utc, updated_at_utc, has_study_pack
            ) VALUES (
                $id, $sha, $fld, $title, $title,
                '', NULL, 0, 'Author', 'Author',
                '', '', '', '2025-01-01', 'ja', '',
                0, 'C:\\Books', 'book.epub', 1000,
                NULL, 1, 100, 1,
                'vertical-rl', $now, $now, 0
            );
            """;
        cmd.Parameters.AddWithValue("$id", bookId);
        cmd.Parameters.AddWithValue("$sha", $"sha256-{bookId}");
        cmd.Parameters.AddWithValue("$fld", (object?)folderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }
}
