using System.Text;
using System.Text.Json;
using ImmersiveReading.Dictionaries;

namespace ImmersiveReading.Plugins.DeckBuilder;

/// <summary>
/// <see cref="IDeckDictionary"/> over the dictionaries the user imported into the app (Yomitan format).
/// jiten.moe resolves words against JMdict; here the imported dictionaries play that role, in their priority
/// order, so a JMdict-derived dictionary gives the same sequence ids and glosses. Frequency ranks and pitch
/// positions come from the term-meta banks of the imported dictionaries.
/// </summary>
public sealed class SqliteDeckDictionary(DictionaryDatabase db) : IDeckDictionary
{
    private const int ChunkSize = 400;

    private sealed record Row(string Expression, string Reading, string ReadingNorm, double Score, string Glossary, long Sequence, int Priority);

    public IReadOnlyDictionary<string, DictEntry> Resolve(IReadOnlyCollection<DictQuery> queries)
    {
        var result = new Dictionary<string, DictEntry>();
        if (queries.Count == 0) return result;

        using var conn = db.CreateConnection();

        // Kana-only lemmas are often stored by reading, so ask for both spellings.
        var lookupForms = queries.Select(q => q.Lemma).Concat(queries.Select(q => DeckParser.ToHiragana(q.Lemma))).Distinct().ToList();
        var byExpression = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
        var byReading = new Dictionary<string, List<Row>>(StringComparer.Ordinal);

        foreach (var chunk in lookupForms.Chunk(ChunkSize))
        {
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                names.Add("$p" + i);
                cmd.Parameters.AddWithValue("$p" + i, chunk[i]);
            }
            string inClause = string.Join(",", names);
            cmd.CommandText = $"""
                SELECT t.expression, t.reading, t.reading_norm, t.score, t.glossary_text, t.sequence, s.priority_order
                FROM dictionary_terms t
                JOIN dictionary_sources s ON s.dictionary_id = t.dictionary_id
                WHERE s.enabled = 1 AND (t.expression IN ({inClause}) OR t.reading_norm IN ({inClause}))
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetDouble(3),
                    reader.GetString(4), reader.GetInt64(5), reader.GetInt32(6));
                Add(byExpression, row.Expression, row);
                Add(byReading, row.ReadingNorm, row);
            }
        }

        var chosen = new Dictionary<string, Row>();
        foreach (var q in queries)
        {
            string lemmaKana = DeckParser.ToHiragana(q.Lemma);
            if (!byExpression.TryGetValue(q.Lemma, out var candidates))
            {
                // Only fall back to a reading match for kana lemmas, a kanji lemma must match its spelling.
                if (!IsKana(q.Lemma) || !byReading.TryGetValue(lemmaKana, out candidates)) continue;
            }

            var best = candidates
                .OrderByDescending(r => q.Reading.Length > 0 && r.ReadingNorm.StartsWith(q.Reading, StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(r => r.Priority)
                .ThenByDescending(r => r.Score)
                .First();
            chosen[q.Key] = best;
        }

        var meta = LoadMeta(conn, chosen.Values.Select(r => r.Expression).Distinct().ToList());

        foreach (var (key, row) in chosen)
        {
            meta.TryGetValue((row.Expression, row.ReadingNorm), out var m);
            result[key] = new DictEntry(
                Expression: row.Expression,
                Reading: DeckParser.ToHiragana(row.ReadingNorm.Length > 0 ? row.ReadingNorm : row.Reading),
                Sequence: row.Sequence,
                Definitions: CleanGlossary(row.Glossary),
                PitchPositions: m is null ? "" : string.Join(",", m.Pitches.Distinct()),
                FrequencyRank: m?.FrequencyRank ?? 0);
        }

        return result;
    }

    private sealed class Meta
    {
        public List<int> Pitches { get; } = [];
        public int FrequencyRank { get; set; }
    }

    private static Dictionary<(string Expression, string ReadingNorm), Meta> LoadMeta(Microsoft.Data.Sqlite.SqliteConnection conn, List<string> expressions)
    {
        var map = new Dictionary<(string, string), Meta>();
        foreach (var chunk in expressions.Chunk(ChunkSize))
        {
            using var cmd = conn.CreateCommand();
            var names = new List<string>();
            for (int i = 0; i < chunk.Length; i++)
            {
                names.Add("$m" + i);
                cmd.Parameters.AddWithValue("$m" + i, chunk[i]);
            }
            cmd.CommandText = $"""
                SELECT m.expression, m.reading_norm, m.mode, m.data_json
                FROM dictionary_term_meta m
                JOIN dictionary_sources s ON s.dictionary_id = m.dictionary_id
                WHERE s.enabled = 1 AND m.expression IN ({string.Join(",", names)})
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string expr = reader.GetString(0);
                string reading = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string mode = reader.GetString(2);
                string json = reader.GetString(3);

                // Meta without a reading is stored under "" and copied onto the specific readings below.
                var key = (expr, reading);
                if (!map.TryGetValue(key, out var meta)) map[key] = meta = new Meta();
                ApplyMeta(meta, mode, json);
            }
        }

        // A reading-less record belongs to all readings: copy it onto the specific ones.
        foreach (var group in map.GroupBy(kv => kv.Key.Item1))
        {
            if (!map.TryGetValue((group.Key, ""), out var general)) continue;
            foreach (var kv in group.Where(kv => kv.Key.Item2.Length > 0))
            {
                kv.Value.Pitches.AddRange(general.Pitches);
                if (kv.Value.FrequencyRank == 0) kv.Value.FrequencyRank = general.FrequencyRank;
            }
        }

        return map.ToDictionary(kv => (kv.Key.Item1, kv.Key.Item2), kv => kv.Value);
    }

    private static void ApplyMeta(Meta meta, string mode, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (mode == "pitch" && root.TryGetProperty("pitches", out var pitches) && pitches.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pitches.EnumerateArray())
                {
                    if (p.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number) meta.Pitches.Add(pos.GetInt32());
                }
            }
            else if (mode == "freq" && root.TryGetProperty("frequency", out var freq))
            {
                int rank = freq.ValueKind switch
                {
                    JsonValueKind.Number => freq.GetInt32(),
                    JsonValueKind.String when int.TryParse(new string(freq.GetString()!.Where(char.IsDigit).ToArray()), out int n) => n,
                    _ => 0
                };
                // Lowest rank across frequency dictionaries is the most common reading of the word.
                if (rank > 0 && (meta.FrequencyRank == 0 || rank < meta.FrequencyRank)) meta.FrequencyRank = rank;
            }
        }
        catch (JsonException)
        {
            // A malformed meta record only costs that one hint.
        }
    }

    /// <summary>Glossary text may hold one gloss per line; a deck cell holds them comma separated.</summary>
    private static string CleanGlossary(string glossary)
    {
        var parts = glossary.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts);
    }

    private static bool IsKana(string text) => text.All(c => SentenceSplitter.IsKana(c) || c == 'ー');

    private static void Add(Dictionary<string, List<Row>> map, string key, Row row)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(row);
    }
}
