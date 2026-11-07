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
              "difficulty": 3,
              "difficultyRaw": 3.93,
              "coverName": "https://cdn.jiten.moe/54961/cover.jpg",
              "mediaType": 4,
              "childrenDeckCount": 12
            }
          ],
          "totalItems": 1
        }
        """;

        using var doc = JsonDocument.Parse(mockJson);
        Assert.True(doc.RootElement.TryGetProperty("data", out var dataArr));
        var first = dataArr.EnumerateArray().First();

        long deckId = first.GetProperty("deckId").GetInt64();
        string title = first.GetProperty("originalTitle").GetString()!;
        double diff = first.GetProperty("difficultyRaw").GetDouble();
        int charCount = first.GetProperty("characterCount").GetInt32();

        Assert.Equal(54961, deckId);
        Assert.Equal("86―エイティシックス―", title);
        Assert.Equal(3.93, diff);
        Assert.Equal(1603952, charCount);
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
