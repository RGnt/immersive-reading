using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ImmersiveReading.Dictionaries.Abstractions;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Dictionaries;

public sealed record KanjiImportResult(
    int CharactersImported,
    int KanjiAliveEnriched,
    int KanjiVgStrokesEnriched
);

public sealed class KanjidicImporter : IDictionaryImporter
{
    public string FormatName => "kanjidic2";

    public bool CanHandle(string filePath) =>
        !string.IsNullOrEmpty(filePath) && (filePath.EndsWith(".xml.gz", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    public DictionaryImportResult Import(string filePath, int priorityOrder = 100, CancellationToken ct = default)
    {
        var res = Import(filePath, kaDataCsvPath: null, kanjivgZipPath: null, ct: ct);
        return new DictionaryImportResult(true, "KANJIDIC2", 0, res.CharactersImported, $"Imported {res.CharactersImported} kanji characters");
    }
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private readonly DictionaryDatabase _db;

    public KanjidicImporter(DictionaryDatabase db)
    {
        _db = db;
    }

    public KanjiImportResult ImportAll(string dataDirectory, CancellationToken ct = default)
    {
        var kanjidicPath = Path.Combine(dataDirectory, "kanjidic2.xml.gz");
        var kaDataPath = Path.Combine(dataDirectory, "kanji-alive", "ka_data.csv");
        var kanjivgPath = Path.Combine(dataDirectory, "kanjivg", "source.zip");

        return Import(kanjidicPath, kaDataPath, kanjivgPath, ct);
    }

    public KanjiImportResult Import(
        string kanjidicGzPath,
        string? kaDataCsvPath = null,
        string? kanjivgZipPath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(kanjidicGzPath))
        {
            throw new FileNotFoundException($"KANJIDIC2 file not found: {kanjidicGzPath}");
        }

        // 1. Load Kanji alive meanings if provided
        var kaMeanings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(kaDataCsvPath) && File.Exists(kaDataCsvPath))
        {
            kaMeanings = LoadKanjiAliveData(kaDataCsvPath);
        }

        int kanjiAliveCount = 0;
        int importedCount = 0;

        using (var conn = _db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO dictionary_kanji (
                    character, on_readings_json, kun_readings_json,
                    primary_meanings, primary_source, kanjidic_meanings,
                    stroke_count, grade, radical, frequency_json, kanjivg_strokes_json
                ) VALUES (
                    $char, $on, $kun, $prim, $src, $kmean, $strokes, $grade, $rad, $freq, NULL
                ) ON CONFLICT(character) DO UPDATE SET
                    on_readings_json = excluded.on_readings_json,
                    kun_readings_json = excluded.kun_readings_json,
                    primary_meanings = excluded.primary_meanings,
                    primary_source = excluded.primary_source,
                    kanjidic_meanings = excluded.kanjidic_meanings,
                    stroke_count = excluded.stroke_count,
                    grade = excluded.grade,
                    radical = excluded.radical,
                    frequency_json = excluded.frequency_json;
                """;

            var pChar = cmd.Parameters.Add("$char", SqliteType.Text);
            var pOn = cmd.Parameters.Add("$on", SqliteType.Text);
            var pKun = cmd.Parameters.Add("$kun", SqliteType.Text);
            var pPrim = cmd.Parameters.Add("$prim", SqliteType.Text);
            var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);
            var pKmean = cmd.Parameters.Add("$kmean", SqliteType.Text);
            var pStrokes = cmd.Parameters.Add("$strokes", SqliteType.Integer);
            var pGrade = cmd.Parameters.Add("$grade", SqliteType.Text);
            var pRad = cmd.Parameters.Add("$rad", SqliteType.Text);
            var pFreq = cmd.Parameters.Add("$freq", SqliteType.Text);

            string rawXml;
            using (var fileStream = File.OpenRead(kanjidicGzPath))
            using (var gzStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (var sr = new StreamReader(gzStream, System.Text.Encoding.UTF8))
            {
                rawXml = sr.ReadToEnd();
            }

            int rootIdx = rawXml.IndexOf("<kanjidic2", StringComparison.Ordinal);
            if (rootIdx > 0)
            {
                rawXml = rawXml[rootIdx..];
            }

            using var stringReader = new StringReader(rawXml);
            using var xmlReader = XmlReader.Create(stringReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true,
                XmlResolver = null
            });

            xmlReader.MoveToContent();
            while (!xmlReader.EOF)
            {
                ct.ThrowIfCancellationRequested();

                if (xmlReader.NodeType == XmlNodeType.Element && xmlReader.Name == "character")
                {
                    var charEl = XNode.ReadFrom(xmlReader) as XElement;
                    if (charEl == null) continue;

                    var literal = charEl.Element("literal")?.Value;
                    if (string.IsNullOrEmpty(literal)) continue;

                    var onReadings = new List<string>();
                    var kunReadings = new List<string>();
                    var kanjidicMeanings = new List<string>();

                    var rmGroup = charEl.Element("reading_meaning")?.Element("rmgroup");
                    if (rmGroup != null)
                    {
                        foreach (var r in rmGroup.Elements("reading"))
                        {
                            var rType = r.Attribute("r_type")?.Value;
                            if (rType == "ja_on")
                            {
                                onReadings.Add(r.Value.Trim());
                            }
                            else if (rType == "ja_kun")
                            {
                                kunReadings.Add(r.Value.Trim());
                            }
                        }

                        foreach (var m in rmGroup.Elements("meaning"))
                        {
                            // Meanings without m_lang are English
                            if (m.Attribute("m_lang") == null)
                            {
                                var mVal = m.Value.Trim();
                                if (!string.IsNullOrEmpty(mVal))
                                {
                                    kanjidicMeanings.Add(mVal);
                                }
                            }
                        }
                    }

                    var misc = charEl.Element("misc");
                    int? strokeCount = null;
                    if (int.TryParse(misc?.Element("stroke_count")?.Value, out var sc))
                    {
                        strokeCount = sc;
                    }

                    var grade = misc?.Element("grade")?.Value;
                    var freq = misc?.Element("freq")?.Value;

                    var radical = charEl.Element("radical")?.Element("rad_value")?.Value;

                    List<string> primaryMeanings;
                    string primarySource;

                    if (kaMeanings.TryGetValue(literal, out var aliveList))
                    {
                        primaryMeanings = aliveList;
                        primarySource = "Kanji alive";
                        kanjiAliveCount++;
                    }
                    else
                    {
                        primaryMeanings = kanjidicMeanings;
                        primarySource = "KANJIDIC2 / EDRDG";
                    }

                    pChar.Value = literal;
                    pOn.Value = JsonSerializer.Serialize(onReadings);
                    pKun.Value = JsonSerializer.Serialize(kunReadings);
                    pPrim.Value = JsonSerializer.Serialize(primaryMeanings);
                    pSrc.Value = primarySource;
                    pKmean.Value = JsonSerializer.Serialize(kanjidicMeanings);
                    pStrokes.Value = (object?)strokeCount ?? DBNull.Value;
                    pGrade.Value = (object?)grade ?? DBNull.Value;
                    pRad.Value = (object?)radical ?? DBNull.Value;
                    pFreq.Value = (object?)freq ?? DBNull.Value;

                    cmd.ExecuteNonQuery();
                    importedCount++;
                }
                else
                {
                    xmlReader.Read();
                }
            }

            tx.Commit();
        }

        int kanjivgCount = 0;
        if (!string.IsNullOrEmpty(kanjivgZipPath) && File.Exists(kanjivgZipPath))
        {
            kanjivgCount = ImportKanjiVgStrokes(kanjivgZipPath, ct);
        }

        return new KanjiImportResult(
            CharactersImported: importedCount,
            KanjiAliveEnriched: kanjiAliveCount,
            KanjiVgStrokesEnriched: kanjivgCount
        );
    }

    public int ImportKanjiVgStrokes(string kanjivgZipPath, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(kanjivgZipPath);
        int count = 0;

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE dictionary_kanji SET kanjivg_strokes_json = $strokes WHERE character = $char;";
        var pChar = cmd.Parameters.Add("$char", SqliteType.Text);
        var pStrokes = cmd.Parameters.Add("$strokes", SqliteType.Text);

        var hexRegex = new Regex(@"([0-9a-fA-F]{5})\.svg$", RegexOptions.Compiled);

        foreach (var entry in zip.Entries)
        {
            var match = hexRegex.Match(entry.Name);
            if (!match.Success) continue;

            if (!int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var codePoint))
            {
                continue;
            }

            string character;
            try
            {
                character = char.ConvertFromUtf32(codePoint);
            }
            catch
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();

            using var s = entry.Open();
            try
            {
                var doc = XDocument.Load(s);
                var paths = doc.Descendants(SvgNs + "path")
                    .Select(p => p.Attribute("d")?.Value)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .ToList();

                if (paths.Count > 0)
                {
                    pChar.Value = character;
                    pStrokes.Value = JsonSerializer.Serialize(paths);
                    var affected = cmd.ExecuteNonQuery();
                    if (affected > 0)
                    {
                        count++;
                    }
                }
            }
            catch
            {
                // Ignore single malformed SVG
            }
        }

        tx.Commit();
        return count;
    }

    public static Dictionary<string, List<string>> LoadKanjiAliveData(string kaDataCsvPath)
    {
        var meanings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var reader = new StreamReader(kaDataCsvPath);

        var headerLine = reader.ReadLine();
        if (string.IsNullOrEmpty(headerLine)) return meanings;

        var headers = headerLine.Split(',');
        int kanjiIdx = Array.IndexOf(headers, "kanji");
        int meaningIdx = Array.IndexOf(headers, "kmeaning");

        if (kanjiIdx < 0 || meaningIdx < 0) return meanings;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Simple CSV line parse respecting quotes
            var fields = ParseCsvLine(line);
            if (fields.Count > Math.Max(kanjiIdx, meaningIdx))
            {
                var kanji = fields[kanjiIdx].Trim();
                var kmeaning = fields[meaningIdx].Trim();
                if (kanji.Length > 0 && kmeaning.Length > 0)
                {
                    meanings[kanji] = SplitMeanings(kmeaning);
                }
            }
        }

        return meanings;
    }

    public static List<string> SplitMeanings(string text)
    {
        var parts = new List<string>();
        int start = 0;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if ((c == ',' || c == ';') && depth == 0)
            {
                var part = text[start..i].Trim();
                if (part.Length > 0) parts.Add(part);
                start = i + 1;
            }
        }
        var last = text[start..].Trim();
        if (last.Length > 0) parts.Add(last);
        return parts;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (line[i] == ',' && !inQuotes)
            {
                result.Add(line[start..i].Trim(' ', '"'));
                start = i + 1;
            }
        }
        result.Add(line[start..].Trim(' ', '"'));
        return result;
    }
}

