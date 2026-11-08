using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Core.Vocabulary;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library;

public sealed class VocabularyTracker : IVocabularyService
{
    private readonly LibraryDatabase _db;

    public VocabularyTracker(LibraryDatabase db)
    {
        _db = db;
    }

    public WordStatus GetStatus(string language, string lemma, string reading)
    {
        var record = GetRecord(language, lemma, reading);
        return record?.Status ?? WordStatus.New;
    }

    public VocabularyRecord? GetRecord(string language, string lemma, string reading)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT language, lemma, reading, status, custom_gloss, times_looked_up, first_seen_book_id, updated_at_utc
            FROM vocabulary_state
            WHERE language = $lang AND lemma = $lemma AND reading = $read;
            """;
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$lemma", lemma);
        cmd.Parameters.AddWithValue("$read", reading);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new VocabularyRecord(
                Language: reader.GetString(0),
                Lemma: reader.GetString(1),
                Reading: reader.GetString(2),
                Status: (WordStatus)reader.GetInt32(3),
                CustomGloss: reader.IsDBNull(4) ? null : reader.GetString(4),
                TimesLookedUp: reader.GetInt32(5),
                FirstSeenBookId: reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAtUtc: reader.GetString(7)
            );
        }

        return null;
    }

    public void SetStatus(string language, string lemma, string reading, WordStatus status, string? customGloss = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vocabulary_state (
                language, lemma, reading, status, custom_gloss, times_looked_up, updated_at_utc
            ) VALUES (
                $lang, $lemma, $read, $status, $gloss, 1, $now
            ) ON CONFLICT(language, lemma, reading) DO UPDATE SET
                status = excluded.status,
                custom_gloss = COALESCE(excluded.custom_gloss, vocabulary_state.custom_gloss),
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$lemma", lemma);
        cmd.Parameters.AddWithValue("$read", reading);
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$gloss", (object?)customGloss ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public void IncrementLookupCount(string language, string lemma, string reading, string? bookId = null)
    {
        var now = DateTime.UtcNow.ToString("o");
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vocabulary_state (
                language, lemma, reading, status, times_looked_up, first_seen_book_id, updated_at_utc
            ) VALUES (
                $lang, $lemma, $read, 0, 1, $bookId, $now
            ) ON CONFLICT(language, lemma, reading) DO UPDATE SET
                times_looked_up = vocabulary_state.times_looked_up + 1,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$lemma", lemma);
        cmd.Parameters.AddWithValue("$read", reading);
        cmd.Parameters.AddWithValue("$bookId", (object?)bookId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<VocabularyRecord> GetWordsByStatus(string language, WordStatus status)
    {
        var list = new List<VocabularyRecord>();
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT language, lemma, reading, status, custom_gloss, times_looked_up, first_seen_book_id, updated_at_utc
            FROM vocabulary_state
            WHERE language = $lang AND status = $status
            ORDER BY updated_at_utc DESC;
            """;
        cmd.Parameters.AddWithValue("$lang", language);
        cmd.Parameters.AddWithValue("$status", (int)status);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new VocabularyRecord(
                Language: reader.GetString(0),
                Lemma: reader.GetString(1),
                Reading: reader.GetString(2),
                Status: (WordStatus)reader.GetInt32(3),
                CustomGloss: reader.IsDBNull(4) ? null : reader.GetString(4),
                TimesLookedUp: reader.GetInt32(5),
                FirstSeenBookId: reader.IsDBNull(6) ? null : reader.GetString(6),
                UpdatedAtUtc: reader.GetString(7)
            ));
        }

        return list;
    }
}
