using ImmersiveReading.Core.Knowledge;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Knowledge;

/// <summary>
/// What the user has told us (name, family, tastes, plans), kept in its own small database so characters, tutors and any other
/// feature can recall it through <see cref="IKnowledgeHub"/>. The owner can list, and really delete, every fact (forgetting removes
/// the row). A fact can be limited to some audiences (one character), so a secret told to one persona is not repeated by another.
/// </summary>
public sealed class UserMemorySource : IWritableKnowledgeSource
{
    public const string Id = "user-memory";
    public const int MaxTextChars = 500;
    public const int MaxFacts = 5000;

    private readonly string _path;
    private readonly TimeProvider _time;

    public UserMemorySource(string dbPath, TimeProvider? time = null)
    {
        _path = dbPath;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        using var conn = Open();
        Exec(conn, """
            CREATE TABLE IF NOT EXISTS user_facts (
                id TEXT PRIMARY KEY, scope_id TEXT NOT NULL, subject TEXT NOT NULL DEFAULT '', text TEXT NOT NULL, text_key TEXT NOT NULL,
                origin TEXT NOT NULL DEFAULT '', shared_with TEXT NOT NULL DEFAULT '',
                created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
                UNIQUE (scope_id, text_key));
            CREATE INDEX IF NOT EXISTS idx_user_facts_updated ON user_facts(scope_id, updated_at_utc DESC);
            """);
    }

    public string SourceId => Id;
    public string DisplayName => "What you told me";
    public IReadOnlyList<string> ScopeKinds { get; } = [KnowledgeScope.UserKind];
    public KnowledgeGating Gating => KnowledgeGating.None;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, DefaultTimeout = 10 }.ToString());
        conn.Open();
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed record Row(string Id, string Subject, string Text, string Origin, IReadOnlyList<string> SharedWith, string Created, string Updated);

    private KnowledgeEntry ToEntry(Row r, double relevance) => new(
        Id, KnowledgeScope.User, r.Id, "fact", r.Subject, r.Text, relevance,
        new Dictionary<string, string>
        {
            ["origin"] = r.Origin,
            ["createdUtc"] = r.Created,
            ["updatedUtc"] = r.Updated,
            ["sharedWith"] = string.Join(",", r.SharedWith)
        });

    private static Row ReadRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries), r.GetString(5), r.GetString(6));

    private const string Columns = "id, subject, text, origin, shared_with, created_at_utc, updated_at_utc";

    public Task<KnowledgeEntry> RememberAsync(FactInput fact, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (fact.Scope.Kind != KnowledgeScope.UserKind) throw new KnowledgeException($"User memory stores facts about the user, not about '{fact.Scope.Kind}'.");
        string text = KnowledgeMatch.Clean(fact.Text);
        if (text.Length == 0) throw new KnowledgeException("A fact needs some text.");
        if (text.Length > MaxTextChars) throw new KnowledgeException($"A fact is at most {MaxTextChars} characters (this one has {text.Length}); state one fact at a time.");
        string key = KnowledgeMatch.Key(text);
        if (key.Length == 0) throw new KnowledgeException("A fact needs some letters or digits.");
        string subject = KnowledgeMatch.Clean(fact.Subject);
        string origin = KnowledgeMatch.Clean(fact.Origin);
        string shared = string.Join('\n', (fact.SharedWith ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string now = _time.GetUtcNow().UtcDateTime.ToString("O");

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        string? id = null;
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM user_facts WHERE scope_id = $s AND text_key = $k;";
            find.Parameters.AddWithValue("$s", fact.Scope.Id);
            find.Parameters.AddWithValue("$k", key);
            id = find.ExecuteScalar() as string;
        }

        if (id is not null)
        {
            // the same fact told again: refresh it (and its audience) instead of storing a duplicate
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE user_facts SET updated_at_utc = $now, subject = CASE WHEN $subject = '' THEN subject ELSE $subject END, shared_with = $shared WHERE id = $id;";
            upd.Parameters.AddWithValue("$now", now);
            upd.Parameters.AddWithValue("$subject", subject);
            upd.Parameters.AddWithValue("$shared", shared);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }
        else
        {
            using (var count = conn.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = "SELECT COUNT(*) FROM user_facts WHERE scope_id = $s;";
                count.Parameters.AddWithValue("$s", fact.Scope.Id);
                if (Convert.ToInt32(count.ExecuteScalar()) >= MaxFacts)
                    throw new KnowledgeException($"There are already {MaxFacts} facts; forget some before adding more.");
            }
            id = "uf-" + Guid.NewGuid().ToString("N")[..16];
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO user_facts (id, scope_id, subject, text, text_key, origin, shared_with, created_at_utc, updated_at_utc) VALUES ($id, $s, $subject, $text, $k, $origin, $shared, $now, $now);";
            ins.Parameters.AddWithValue("$id", id);
            ins.Parameters.AddWithValue("$s", fact.Scope.Id);
            ins.Parameters.AddWithValue("$subject", subject);
            ins.Parameters.AddWithValue("$text", text);
            ins.Parameters.AddWithValue("$k", key);
            ins.Parameters.AddWithValue("$origin", origin);
            ins.Parameters.AddWithValue("$shared", shared);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }

        Row row;
        using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT {Columns} FROM user_facts WHERE id = $id;";
            read.Parameters.AddWithValue("$id", id);
            using var r = read.ExecuteReader();
            r.Read();
            row = ReadRow(r);
        }
        tx.Commit();
        return Task.FromResult(ToEntry(row, 0));
    }

    public Task<bool> ForgetAsync(string factId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM user_facts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", factId);
        return Task.FromResult(cmd.ExecuteNonQuery() > 0);
    }

    public Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.Scope.Kind != KnowledgeScope.UserKind) return Task.FromResult<IReadOnlyList<KnowledgeEntry>>([]);
        if (query.Kinds is { Count: > 0 } && !query.Kinds.Contains("fact", StringComparer.Ordinal)) return Task.FromResult<IReadOnlyList<KnowledgeEntry>>([]);
        int limit = Math.Clamp(query.Limit, 1, 200);

        var rows = new List<Row>();
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT {Columns} FROM user_facts WHERE scope_id = $s ORDER BY updated_at_utc DESC, id LIMIT {MaxFacts};";
            cmd.Parameters.AddWithValue("$s", query.Scope.Id);
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(ReadRow(r));
        }

        // the owner (no audience) sees everything; a character sees what is meant for everyone and what was shared with it
        rows = rows.Where(r => query.Audience is null || r.SharedWith.Count == 0 || r.SharedWith.Contains(query.Audience, StringComparer.Ordinal)).ToList();

        string text = KnowledgeMatch.Clean(query.Text);
        if (text.Length == 0) return Task.FromResult<IReadOnlyList<KnowledgeEntry>>(rows.Take(limit).Select(r => ToEntry(r, 0)).ToList());

        var scores = KnowledgeMatch.Score(text, rows.Select(r => r.Subject.Length == 0 ? r.Text : r.Subject + " " + r.Text).ToList());
        var found = rows.Select((r, i) => (Row: r, Score: scores[i]))
            .Where(x => x.Score >= KnowledgeMatch.MinScore)
            .OrderByDescending(x => x.Score)       // stable: equal scores keep the newest first
            .Take(limit)
            .Select(x => ToEntry(x.Row, Math.Round(x.Score, 4)))
            .ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeEntry>>(found);
    }
}
