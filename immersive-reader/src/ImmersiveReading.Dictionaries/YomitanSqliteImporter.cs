using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmersiveReading.Plugins.Japanese;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries;

public sealed record YomitanImportResult(
    string DictionaryId,
    string Title,
    string Revision,
    int TermsCount,
    int MetaCount,
    int KanjiCount
);

public sealed class YomitanSqliteImporter
{
    private static readonly Regex BankRegex = new(@"^(term|kanji|term_meta|kanji_meta|tag)_bank_([1-9][0-9]*)\.json$", RegexOptions.Compiled);
    private readonly DictionaryDatabase _db;

    public YomitanSqliteImporter(DictionaryDatabase db)
    {
        _db = db;
    }

    public YomitanImportResult ImportZip(string zipPath, int priorityOrder = 100, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"Yomitan zip not found: {zipPath}");
        }

        string checksum;
        using (var fs = File.OpenRead(zipPath))
        {
            var hashBytes = SHA256.HashData(fs);
            checksum = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        var dictionaryId = "yomitan-" + checksum;

        using var zip = ZipFile.OpenRead(zipPath);
        var indexEntry = zip.GetEntry("index.json")
            ?? throw new InvalidOperationException($"Invalid Yomitan zip: missing index.json in {zipPath}");

        using var indexStream = indexEntry.Open();
        using var indexDoc = JsonDocument.Parse(indexStream);
        var root = indexDoc.RootElement;

        var title = root.GetProperty("title").GetString()
            ?? throw new InvalidOperationException("index.json is missing required 'title'");
        var revision = root.TryGetProperty("revision", out var revProp) ? revProp.GetString() ?? "1" : "1";

        int format = 0;
        if (root.TryGetProperty("format", out var formatProp))
        {
            format = formatProp.GetInt32();
        }
        else if (root.TryGetProperty("version", out var verProp))
        {
            format = verProp.GetInt32();
        }

        if (format != 3)
        {
            throw new InvalidOperationException($"Unsupported Yomitan format: {format}; expected 3");
        }

        var author = root.TryGetProperty("author", out var authProp) ? authProp.GetString() : null;
        var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        var attribution = root.TryGetProperty("attribution", out var attrProp) ? attrProp.GetString() : null;
        var sourceLang = root.TryGetProperty("sourceLanguage", out var slangProp) ? slangProp.GetString() ?? "ja" : "ja";
        var targetLang = root.TryGetProperty("targetLanguage", out var tlangProp) ? tlangProp.GetString() ?? "en" : "en";

        // Collect notice files
        var notices = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            var lower = entry.Name.ToLowerInvariant();
            if (lower.StartsWith("license") || lower.StartsWith("licence") ||
                lower.StartsWith("readme") || lower.StartsWith("notice"))
            {
                if (entry.Length <= 1024 * 1024)
                {
                    using var s = entry.Open();
                    using var reader = new StreamReader(s, Encoding.UTF8);
                    notices[entry.Name] = reader.ReadToEnd();
                }
            }
        }

        var bankEntries = zip.Entries
            .Select(e => new { Entry = e, Match = BankRegex.Match(e.Name) })
            .Where(x => x.Match.Success)
            .OrderBy(x => x.Match.Groups[1].Value)
            .ThenBy(x => int.Parse(x.Match.Groups[2].Value))
            .ToList();

        int termsCount = 0;
        int metaCount = 0;
        int kanjiCount = 0;
        var nowUtc = DateTime.UtcNow.ToString("o");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        // 1. Insert or update dictionary_sources
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dictionary_sources (
                    dictionary_id, title, revision, language, target_language,
                    priority_order, enabled, author, url, attribution,
                    notices_json, bank_counts_json, imported_at_utc
                ) VALUES (
                    $id, $title, $revision, $lang, $tlang,
                    $prio, 1, $author, $url, $attr,
                    $notices, '{}', $imported
                ) ON CONFLICT(dictionary_id) DO UPDATE SET
                    title = excluded.title,
                    revision = excluded.revision,
                    priority_order = excluded.priority_order,
                    imported_at_utc = excluded.imported_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", dictionaryId);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$revision", revision);
            cmd.Parameters.AddWithValue("$lang", sourceLang);
            cmd.Parameters.AddWithValue("$tlang", targetLang);
            cmd.Parameters.AddWithValue("$prio", priorityOrder);
            cmd.Parameters.AddWithValue("$author", (object?)author ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attr", (object?)attribution ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$notices", JsonSerializer.Serialize(notices));
            cmd.Parameters.AddWithValue("$imported", nowUtc);
            cmd.ExecuteNonQuery();
        }

        // Delete previous terms / meta for this dictionary if re-importing
        using (var delCmd = conn.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM dictionary_terms WHERE dictionary_id = $id; DELETE FROM dictionary_term_meta WHERE dictionary_id = $id;";
            delCmd.Parameters.AddWithValue("$id", dictionaryId);
            delCmd.ExecuteNonQuery();
        }

        // Prepare term insert command
        using var termCmd = conn.CreateCommand();
        termCmd.Transaction = tx;
        termCmd.CommandText = """
            INSERT INTO dictionary_terms (
                dictionary_id, expression, reading, reading_norm,
                definition_tags, rules, score, glossary_json,
                glossary_text, sequence, term_tags, bank_name, bank_row
            ) VALUES (
                $dictId, $expr, $read, $readNorm,
                $defTags, $rules, $score, $glossJson,
                $glossText, $seq, $termTags, $bankName, $bankRow
            );
            """;
        var pDictId = termCmd.Parameters.Add("$dictId", SqliteType.Text);
        var pExpr = termCmd.Parameters.Add("$expr", SqliteType.Text);
        var pRead = termCmd.Parameters.Add("$read", SqliteType.Text);
        var pReadNorm = termCmd.Parameters.Add("$readNorm", SqliteType.Text);
        var pDefTags = termCmd.Parameters.Add("$defTags", SqliteType.Text);
        var pRules = termCmd.Parameters.Add("$rules", SqliteType.Text);
        var pScore = termCmd.Parameters.Add("$score", SqliteType.Real);
        var pGlossJson = termCmd.Parameters.Add("$glossJson", SqliteType.Text);
        var pGlossText = termCmd.Parameters.Add("$glossText", SqliteType.Text);
        var pSeq = termCmd.Parameters.Add("$seq", SqliteType.Integer);
        var pTermTags = termCmd.Parameters.Add("$termTags", SqliteType.Text);
        var pBankName = termCmd.Parameters.Add("$bankName", SqliteType.Text);
        var pBankRow = termCmd.Parameters.Add("$bankRow", SqliteType.Integer);
        pDictId.Value = dictionaryId;

        // Prepare meta insert command
        using var metaCmd = conn.CreateCommand();
        metaCmd.Transaction = tx;
        metaCmd.CommandText = """
            INSERT INTO dictionary_term_meta (
                dictionary_id, expression, reading_norm, mode, data_json
            ) VALUES (
                $dictId, $expr, $readNorm, $mode, $data
            );
            """;
        var pmDictId = metaCmd.Parameters.Add("$dictId", SqliteType.Text);
        var pmExpr = metaCmd.Parameters.Add("$expr", SqliteType.Text);
        var pmReadNorm = metaCmd.Parameters.Add("$readNorm", SqliteType.Text);
        var pmMode = metaCmd.Parameters.Add("$mode", SqliteType.Text);
        var pmData = metaCmd.Parameters.Add("$data", SqliteType.Text);
        pmDictId.Value = dictionaryId;

        // Prepare kanji insert command
        using var kanjiCmd = conn.CreateCommand();
        kanjiCmd.Transaction = tx;
        kanjiCmd.CommandText = """
            INSERT INTO dictionary_kanji (
                character, on_readings_json, kun_readings_json,
                primary_meanings, primary_source, kanjidic_meanings,
                stroke_count, grade, radical, frequency_json, kanjivg_strokes_json
            ) VALUES (
                $char, $on, $kun, $prim, $src, $kmean, $strokes, $grade, $rad, $freq, NULL
            ) ON CONFLICT(character) DO UPDATE SET
                on_readings_json = CASE WHEN excluded.on_readings_json != '[]' THEN excluded.on_readings_json ELSE dictionary_kanji.on_readings_json END,
                kun_readings_json = CASE WHEN excluded.kun_readings_json != '[]' THEN excluded.kun_readings_json ELSE dictionary_kanji.kun_readings_json END,
                primary_meanings = CASE WHEN dictionary_kanji.primary_meanings = '[]' THEN excluded.primary_meanings ELSE dictionary_kanji.primary_meanings END;
            """;
        var pkChar = kanjiCmd.Parameters.Add("$char", SqliteType.Text);
        var pkOn = kanjiCmd.Parameters.Add("$on", SqliteType.Text);
        var pkKun = kanjiCmd.Parameters.Add("$kun", SqliteType.Text);
        var pkPrim = kanjiCmd.Parameters.Add("$prim", SqliteType.Text);
        var pkSrc = kanjiCmd.Parameters.Add("$src", SqliteType.Text);
        var pkKmean = kanjiCmd.Parameters.Add("$kmean", SqliteType.Text);
        var pkStrokes = kanjiCmd.Parameters.Add("$strokes", SqliteType.Integer);
        var pkGrade = kanjiCmd.Parameters.Add("$grade", SqliteType.Text);
        var pkRad = kanjiCmd.Parameters.Add("$rad", SqliteType.Text);
        var pkFreq = kanjiCmd.Parameters.Add("$freq", SqliteType.Text);

        foreach (var item in bankEntries)
        {
            ct.ThrowIfCancellationRequested();
            var kind = item.Match.Groups[1].Value;

            using var entryStream = item.Entry.Open();
            using var bankDoc = JsonDocument.Parse(entryStream);

            int rowNumber = 0;
            foreach (var row in bankDoc.RootElement.EnumerateArray())
            {
                rowNumber++;
                if (kind == "term")
                {
                    // [expression, reading, definitionTags, rules, score, glossaryArray, sequence, termTags]
                    var expr = row[0].GetString() ?? "";
                    var reading = row[1].GetString();
                    if (string.IsNullOrEmpty(reading))
                    {
                        reading = expr;
                    }
                    var readingNorm = KanaNormalizer.ToHiragana(reading);
                    var defTags = row[2].GetString() ?? "";
                    var rules = row[3].GetString() ?? "";
                    var score = row[4].GetDouble();
                    var glossElem = row[5];
                    var glossJson = glossElem.GetRawText();
                    var glossText = ExtractGlossaryText(glossElem);
                    var seq = row[6].GetInt32();
                    var termTags = row[7].GetString() ?? "";

                    pExpr.Value = expr;
                    pRead.Value = reading;
                    pReadNorm.Value = readingNorm;
                    pDefTags.Value = defTags;
                    pRules.Value = rules;
                    pScore.Value = score;
                    pGlossJson.Value = glossJson;
                    pGlossText.Value = glossText;
                    pSeq.Value = seq;
                    pTermTags.Value = termTags;
                    pBankName.Value = item.Entry.Name;
                    pBankRow.Value = rowNumber;

                    termCmd.ExecuteNonQuery();
                    termsCount++;
                }
                else if (kind == "term_meta")
                {
                    // [expression, mode, data]
                    var expr = row[0].GetString() ?? "";
                    var mode = row[1].GetString() ?? "";
                    var data = row[2];
                    var dataJson = data.GetRawText();

                    string? readingNorm = null;
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reading", out var rProp))
                    {
                        var rawR = rProp.GetString();
                        if (!string.IsNullOrEmpty(rawR))
                        {
                            readingNorm = KanaNormalizer.ToHiragana(rawR);
                        }
                    }

                    pmExpr.Value = expr;
                    pmReadNorm.Value = (object?)readingNorm ?? DBNull.Value;
                    pmMode.Value = mode;
                    pmData.Value = dataJson;

                    metaCmd.ExecuteNonQuery();
                    metaCount++;
                }
                else if (kind == "kanji")
                {
                    // [character, on, kun, tags, meanings, stats]
                    var ch = row[0].GetString() ?? "";
                    var onStr = row[1].GetString() ?? "";
                    var kunStr = row[2].GetString() ?? "";
                    var onArray = onStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var kunArray = kunStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    var meanings = new List<string>();
                    if (row[4].ValueKind == JsonValueKind.Array)
                    {
                        foreach (var m in row[4].EnumerateArray())
                        {
                            if (m.ValueKind == JsonValueKind.String)
                            {
                                meanings.Add(m.GetString()!);
                            }
                        }
                    }

                    int? strokeCount = null;
                    if (row[5].ValueKind == JsonValueKind.Object && row[5].TryGetProperty("strokes", out var sProp))
                    {
                        if (int.TryParse(sProp.GetString(), out var sc))
                        {
                            strokeCount = sc;
                        }
                    }

                    pkChar.Value = ch;
                    pkOn.Value = JsonSerializer.Serialize(onArray);
                    pkKun.Value = JsonSerializer.Serialize(kunArray);
                    pkPrim.Value = JsonSerializer.Serialize(meanings);
                    pkSrc.Value = title;
                    pkKmean.Value = JsonSerializer.Serialize(meanings);
                    pkStrokes.Value = (object?)strokeCount ?? DBNull.Value;
                    pkGrade.Value = DBNull.Value;
                    pkRad.Value = DBNull.Value;
                    pkFreq.Value = DBNull.Value;

                    kanjiCmd.ExecuteNonQuery();
                    kanjiCount++;
                }
            }
        }

        tx.Commit();

        return new YomitanImportResult(
            DictionaryId: dictionaryId,
            Title: title,
            Revision: revision,
            TermsCount: termsCount,
            MetaCount: metaCount,
            KanjiCount: kanjiCount
        );
    }

    public static string ExtractGlossaryText(JsonElement element)
    {
        var sb = new StringBuilder();
        ExtractGlossaryTextRecursive(element, sb);
        return sb.ToString().Trim();
    }

    private static void ExtractGlossaryTextRecursive(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(element.GetString());
                break;

            case JsonValueKind.Array:
                bool first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append("; ");
                    }
                    ExtractGlossaryTextRecursive(item, sb);
                    first = false;
                }
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text")
                {
                    if (element.TryGetProperty("text", out var textProp))
                    {
                        sb.Append(textProp.GetString());
                    }
                }
                else if (element.TryGetProperty("content", out var contentProp))
                {
                    ExtractGlossaryTextRecursive(contentProp, sb);
                }
                else if (element.TryGetProperty("tag", out var tagProp))
                {
                    var tag = tagProp.GetString();
                    if (tag == "br")
                    {
                        sb.Append('\n');
                    }
                    else if (tag == "img")
                    {
                        sb.Append(element.TryGetProperty("alt", out var alt) ? alt.GetString() : "[image]");
                    }
                }
                break;
        }
    }

    public YomitanImportResult ImportJmdictDeckCsvs(
        IEnumerable<string> csvPaths,
        string dictionaryId = "dict-starter-jmdict",
        string title = "Starter JMdict Vocabulary",
        int priorityOrder = 500)
    {
        int termsCount = 0;
        var seenKeys = new HashSet<(string Expr, string Reading)>();

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();

        using (var delTerms = conn.CreateCommand())
        {
            delTerms.Transaction = tx;
            delTerms.CommandText = "DELETE FROM dictionary_terms WHERE dictionary_id = $id;";
            delTerms.Parameters.AddWithValue("$id", dictionaryId);
            delTerms.ExecuteNonQuery();
        }

        using (var insSource = conn.CreateCommand())
        {
            insSource.Transaction = tx;
            insSource.CommandText = """
                INSERT INTO dictionary_sources (dictionary_id, title, revision, language, target_language, priority_order, enabled, imported_at_utc)
                VALUES ($id, $title, 'starter-csv-1', 'ja', 'en', $prio, 1, $now)
                ON CONFLICT(dictionary_id) DO UPDATE SET
                    title = excluded.title,
                    priority_order = excluded.priority_order,
                    imported_at_utc = excluded.imported_at_utc;
                """;
            insSource.Parameters.AddWithValue("$id", dictionaryId);
            insSource.Parameters.AddWithValue("$title", title);
            insSource.Parameters.AddWithValue("$prio", priorityOrder);
            insSource.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            insSource.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO dictionary_terms (
                dictionary_id, expression, reading, reading_norm,
                definition_tags, rules, score, glossary_json, glossary_text, sequence, term_tags, bank_name, bank_row
            ) VALUES (
                $dictId, $expr, $read, $readNorm, '', '', $score, $gJson, $gText, $seq, '', 'starter_csv', $row
            );
            """;

        var pDictId = cmd.Parameters.Add("$dictId", SqliteType.Text);
        var pExpr = cmd.Parameters.Add("$expr", SqliteType.Text);
        var pRead = cmd.Parameters.Add("$read", SqliteType.Text);
        var pReadNorm = cmd.Parameters.Add("$readNorm", SqliteType.Text);
        var pScore = cmd.Parameters.Add("$score", SqliteType.Real);
        var pGJson = cmd.Parameters.Add("$gJson", SqliteType.Text);
        var pGText = cmd.Parameters.Add("$gText", SqliteType.Text);
        var pSeq = cmd.Parameters.Add("$seq", SqliteType.Integer);
        var pRow = cmd.Parameters.Add("$row", SqliteType.Integer);

        pDictId.Value = dictionaryId;

        foreach (var path in csvPaths)
        {
            if (!File.Exists(path)) continue;
            using var reader = new StreamReader(path, Encoding.UTF8);
            string? header = reader.ReadLine();
            if (header is null) continue;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var cols = ParseCsvRow(line);
                if (cols.Count < 7) continue;

                string expr = cols[0].Trim();
                string reading = cols[2].Trim();
                if (string.IsNullOrEmpty(expr)) continue;
                if (string.IsNullOrEmpty(reading)) reading = expr;

                if (!seenKeys.Add((expr, reading))) continue;

                string defsRaw = cols[6].Trim();
                if (string.IsNullOrEmpty(defsRaw)) continue;

                string glossText = defsRaw.Replace(",", "; ");
                string readingNorm = KanaNormalizer.ToHiragana(reading);
                double score = int.TryParse(cols[3], out int occ) ? occ : 1.0;
                int seq = cols.Count > 8 && int.TryParse(cols[8], out int jmdId) ? jmdId : 0;

                pExpr.Value = expr;
                pRead.Value = reading;
                pReadNorm.Value = readingNorm;
                pScore.Value = score;
                pGJson.Value = JsonSerializer.Serialize(new[] { glossText });
                pGText.Value = glossText;
                pSeq.Value = seq;
                pRow.Value = termsCount;

                cmd.ExecuteNonQuery();
                termsCount++;
            }
        }

        tx.Commit();
        return new YomitanImportResult(dictionaryId, title, "starter-csv-1", termsCount, 0, 0);
    }

    private static List<string> ParseCsvRow(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }
}

