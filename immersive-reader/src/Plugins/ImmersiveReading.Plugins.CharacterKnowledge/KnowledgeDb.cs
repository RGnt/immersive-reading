using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <summary>
/// Read-only view of a knowledge database written by the Python builder. **No gating logic lives here** (spec 18, FR-20): the
/// builder precomputes a gated profile snapshot for every chapter boundary and this class only looks the right row up:
/// the latest snapshot at or before the reader's chapter, never a later one.
/// </summary>
public sealed class KnowledgeDb(string path)
{
    public string Path { get; } = path;
    public bool Exists => File.Exists(Path);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,           // no pooled handle: the folder must stay deletable on Windows
            DefaultTimeout = 5         // the builder may be committing a chapter right now
        }.ToString());
        conn.Open();
        return conn;
    }

    public KnowledgeStatus GetStatus()
    {
        if (!Exists) return new KnowledgeStatus([], new KnowledgeCounts(0, 0, 0, 0));
        try
        {
            using var conn = Open();
            var chapters = new List<ChapterState>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT volume, chapter, title, status, ops_applied, ops_rejected, error, model, built_at, text_sha FROM chapters ORDER BY volume, chapter";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    chapters.Add(new ChapterState(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                        r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9)));
                }
            }
            var counts = new KnowledgeCounts(Count(conn, "entities"), Count(conn, "relations"), Count(conn, "events"), Count(conn, "profile_snapshots"),
                CountDistinct(conn, "story_event_cards", "event_id"), CountDistinct(conn, "story_item_cards", "item_id"));
            return new KnowledgeStatus(chapters, counts, ReadStoryChapters(conn));
        }
        catch (SqliteException)
        {
            return new KnowledgeStatus([], new KnowledgeCounts(0, 0, 0, 0));    // not created yet, or being created
        }
    }

    private static int Count(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>The story tables exist from schema 2 on; a database the builder has not migrated yet has none, which just means "no story data".</summary>
    private static int CountDistinct(SqliteConnection conn, string table, string column)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(DISTINCT {column}) FROM {table}";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static List<StoryChapterState> ReadStoryChapters(SqliteConnection conn)
    {
        var rows = new List<StoryChapterState>();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT volume, chapter, status, error, text_sha FROM story_chapters ORDER BY volume, chapter";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new StoryChapterState(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        }
        catch (SqliteException)
        {
            rows.Clear();
        }
        return rows;
    }

    /// <summary>Characters the reader knows when about to read chapter <paramref name="chapter"/> (0-based) of volume <paramref name="volume"/>.</summary>
    public IReadOnlyList<CharacterEntry> ListCharacters(int volume, int chapter)
    {
        if (!Exists) return [];
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT character_id, display_name, prominence FROM profile_snapshots
                WHERE (volume, chapter) = (
                    SELECT volume, chapter FROM profile_snapshots
                    WHERE volume < $v OR (volume = $v AND chapter <= $c)
                    ORDER BY volume DESC, chapter DESC LIMIT 1)
                ORDER BY prominence DESC, character_id
                """;
            cmd.Parameters.AddWithValue("$v", volume);
            cmd.Parameters.AddWithValue("$c", chapter);
            var list = new List<CharacterEntry>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new CharacterEntry(r.GetString(0), r.GetString(1), r.GetInt32(2)));
            return list;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    /// <summary>Any member id of a character's identity group works (the masked man and the teacher he turns out to be share one profile).</summary>
    public CharacterProfile? GetProfile(int volume, int chapter, string characterId)
    {
        if (!Exists) return null;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.character_id, s.display_name, s.profile_json, s.block_text FROM profile_snapshots s
                JOIN profile_snapshot_members m ON m.character_id = s.character_id AND m.volume = s.volume AND m.chapter = s.chapter
                WHERE (s.volume < $v OR (s.volume = $v AND s.chapter <= $c)) AND m.member_id = $id
                ORDER BY s.volume DESC, s.chapter DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$v", volume);
            cmd.Parameters.AddWithValue("$c", chapter);
            cmd.Parameters.AddWithValue("$id", characterId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            using var doc = JsonDocument.Parse(r.GetString(2));
            return new CharacterProfile(r.GetString(0), r.GetString(1), doc.RootElement.Clone(), r.GetString(3));
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ story cards (events, items): spec 22
    // Same principle as the profiles: the builder stored one card per event / item and *change* (valid from a chapter boundary on);
    // the reader sees, for each one, the latest card at or before their chapter. A card whose text would have named someone the
    // reader has not met was never stored. Nothing here decides what is visible beyond that lookup.

    private const string LatestCardSql = """
        SELECT c.card_json FROM {0} c
        WHERE (c.volume, c.chapter) = (
            SELECT d.volume, d.chapter FROM {0} d
            WHERE d.{1} = c.{1} AND (d.volume < $v OR (d.volume = $v AND d.chapter <= $c))
            ORDER BY d.volume DESC, d.chapter DESC LIMIT 1)
        ORDER BY c.sort_key, c.{1}
        """;

    private static List<JsonElement> ReadCards(SqliteConnection conn, string table, string column, int volume, int chapter)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = string.Format(LatestCardSql, table, column);
        cmd.Parameters.AddWithValue("$v", volume);
        cmd.Parameters.AddWithValue("$c", chapter);
        var cards = new List<JsonElement>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            using var doc = JsonDocument.Parse(r.GetString(0));
            cards.Add(doc.RootElement.Clone());
        }
        return cards;
    }

    /// <summary>Entity ids of the identity group a character id belongs to at the reader's position (the id itself when there is no snapshot).</summary>
    private static HashSet<string> MemberIds(SqliteConnection conn, int volume, int chapter, string characterId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.character_id, m.volume, m.chapter FROM profile_snapshot_members m
            WHERE m.member_id = $id AND (m.volume < $v OR (m.volume = $v AND m.chapter <= $c))
            ORDER BY m.volume DESC, m.chapter DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", characterId);
        cmd.Parameters.AddWithValue("$v", volume);
        cmd.Parameters.AddWithValue("$c", chapter);
        string? group = null;
        int groupVolume = 0, groupChapter = 0;
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read()) { group = r.GetString(0); groupVolume = r.GetInt32(1); groupChapter = r.GetInt32(2); }
        }
        var ids = new HashSet<string> { characterId };
        if (group is null) return ids;
        using var members = conn.CreateCommand();
        members.CommandText = "SELECT member_id FROM profile_snapshot_members WHERE character_id = $g AND volume = $gv AND chapter = $gc";
        members.Parameters.AddWithValue("$g", group);
        members.Parameters.AddWithValue("$gv", groupVolume);
        members.Parameters.AddWithValue("$gc", groupChapter);
        using var rows = members.ExecuteReader();
        while (rows.Read()) ids.Add(rows.GetString(0));
        return ids;
    }

    /// <summary>True when any of the entities (<c>{ "ids": [...] }</c>) in <paramref name="entities"/> is one of <paramref name="members"/>.</summary>
    private static bool AnyMember(IEnumerable<JsonElement> entities, HashSet<string> members)
        => entities.Any(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array
                             && ids.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.String && members.Contains(i.GetString()!)));

    /// <summary>Events the reader knows when about to read chapter <paramref name="chapter"/> of volume <paramref name="volume"/>, newest first.</summary>
    public StoryEventList ListEvents(int volume, int chapter, string? characterId = null, string? kind = null, int limit = 200)
    {
        if (!Exists) return new StoryEventList(0, []);
        try
        {
            using var conn = Open();
            var cards = ReadCards(conn, "story_event_cards", "event_id", volume, chapter);
            if (!string.IsNullOrWhiteSpace(kind))
                cards = cards.Where(c => c.TryGetProperty("kind", out var k) && k.GetString() == kind).ToList();
            if (!string.IsNullOrWhiteSpace(characterId))
            {
                var members = MemberIds(conn, volume, chapter, characterId);
                cards = cards.Where(c => c.TryGetProperty("participants", out var p) && p.ValueKind == JsonValueKind.Array && AnyMember(p.EnumerateArray(), members)).ToList();
            }
            cards.Reverse();
            return new StoryEventList(cards.Count, limit > 0 ? cards.Take(limit).ToList() : cards);
        }
        catch (SqliteException)
        {
            return new StoryEventList(0, []);      // no story data yet (or a database the builder has not migrated)
        }
    }

    /// <summary>Items the reader knows (description, current holder, history); with <paramref name="holderId"/> only what that character has right now.</summary>
    public IReadOnlyList<JsonElement> ListItems(int volume, int chapter, string? holderId = null)
    {
        if (!Exists) return [];
        try
        {
            using var conn = Open();
            var cards = ReadCards(conn, "story_item_cards", "item_id", volume, chapter);
            if (string.IsNullOrWhiteSpace(holderId)) return cards;
            var members = MemberIds(conn, volume, chapter, holderId);
            return cards.Where(c => c.TryGetProperty("holder", out var h) && AnyMember([h], members)).ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }
}
