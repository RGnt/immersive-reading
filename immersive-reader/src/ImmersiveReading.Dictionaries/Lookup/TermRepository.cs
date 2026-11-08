using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries.Lookup;

internal sealed record TermRow(
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

internal sealed record MetaRow(string Expression, string? ReadingNorm, string Mode, string DataJson, string DictTitle);

internal sealed class TermRepository
{
    private readonly DictionaryDatabase _dictDb;

    public TermRepository(DictionaryDatabase dictDb)
    {
        _dictDb = dictDb;
    }

    public List<TermRow> QueryTermsFromDb(IReadOnlyList<string> forms, CancellationToken ct)
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

    public Dictionary<string, List<MetaRow>> QueryTermMetaFromDb(IReadOnlyList<string> expressions, CancellationToken ct)
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

    public List<KanjiEntryViewModel> QueryKanjiFromDb(IReadOnlyList<string> kanjiChars, CancellationToken ct)
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

        foreach (var ch in kanjiChars)
        {
            if (foundMap.TryGetValue(ch, out var vm))
            {
                result.Add(vm);
            }
        }

        return result;
    }
}

