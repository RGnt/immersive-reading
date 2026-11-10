using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Infrastructure;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PythonChapterPosTagger
{
    private readonly IPythonEnvironmentResolver _envResolver;

    public PythonChapterPosTagger(IPythonEnvironmentResolver? envResolver = null)
    {
        _envResolver = envResolver ?? PythonEnvironmentResolver.Instance;
    }

    public string? ResolvePythonExe(string? configuredPython = null)
        => _envResolver.ResolvePythonExe(configuredPython);

    public string? ResolveExtractorDir()
        => _envResolver.ResolveExtractorDir();

    public (IReadOnlyDictionary<int, PosClassification>? Classifications, string? RawJson) TagChapter(
        string bookId,
        string chapterHref,
        IReadOnlyList<EnrichedTokenEntry> tokens,
        string? configuredPython = null,
        int timeoutMs = 5000)
    {
        string? pythonExe = ResolvePythonExe(configuredPython);
        string? extractorDir = ResolveExtractorDir();

        if (string.IsNullOrEmpty(pythonExe) || tokens.Count == 0)
        {
            return (null, null);
        }

        try
        {
            var tokenPayloads = new List<object>(tokens.Count);
            foreach (var t in tokens)
            {
                tokenPayloads.Add(new
                {
                    id = t.TokenId,
                    surface = t.Token.Surface,
                    pos = t.Token.PartOfSpeech,
                    posSub = t.Token.PartOfSpeechSub,
                    conjType = t.Token.ConjugationType
                });
            }

            string inputJson = JsonSerializer.Serialize(new
            {
                bookId,
                chapterHref,
                tokens = tokenPayloads
            });

            var utf8NoBom = new UTF8Encoding(false);
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = extractorDir ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8NoBom,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("immersive.cli");
            psi.ArgumentList.Add("pos-chapter");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using (var sw = proc.StandardInput)
            {
                sw.Write(inputJson);
                sw.Flush();
            }

            string outputJson = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                return (null, null);
            }

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(outputJson))
            {
                return (null, $"exit={proc.ExitCode}, err={err}, out={outputJson}");
            }

            var dict = ParseClassificationsJson(outputJson);
            return (dict, outputJson);
        }
        catch (Exception ex)
        {
            return (null, $"exception: {ex.Message}");
        }
    }

    public (IReadOnlyDictionary<int, SudachiParagraph>? Paragraphs, string? RawJson) TagParagraphs(
        string bookId,
        string chapterHref,
        IReadOnlyList<(int Id, string Text)> paragraphs,
        string? configuredPython = null,
        int timeoutMs = 10000)
    {
        string? pythonExe = ResolvePythonExe(configuredPython);
        string? extractorDir = ResolveExtractorDir();

        if (string.IsNullOrEmpty(pythonExe) || paragraphs.Count == 0)
        {
            var fallback = FallbackTagParagraphs(paragraphs);
            return (fallback, null);
        }

        try
        {
            var pList = new List<object>(paragraphs.Count);
            foreach (var p in paragraphs)
            {
                pList.Add(new { id = p.Id, text = p.Text });
            }

            string inputJson = JsonSerializer.Serialize(new
            {
                bookId,
                chapterHref,
                paragraphs = pList
            });

            var utf8NoBom = new UTF8Encoding(false);
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = extractorDir ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = utf8NoBom,
                StandardOutputEncoding = utf8NoBom,
                StandardErrorEncoding = utf8NoBom
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("immersive.cli");
            psi.ArgumentList.Add("pos-chapter");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            using (var sw = proc.StandardInput)
            {
                sw.Write(inputJson);
                sw.Flush();
            }

            string outputJson = proc.StandardOutput.ReadToEnd();
            string err = proc.StandardError.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                var fallback = FallbackTagParagraphs(paragraphs);
                return (fallback, null);
            }

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(outputJson))
            {
                var fallback = FallbackTagParagraphs(paragraphs);
                return (fallback, null);
            }

            var dict = ParseParagraphsJson(outputJson);
            return (dict, outputJson);
        }
        catch
        {
            var fallback = FallbackTagParagraphs(paragraphs);
            return (fallback, null);
        }
    }

    public static IReadOnlyDictionary<int, SudachiParagraph> ParseParagraphsJson(string json)
    {
        var result = new Dictionary<int, SudachiParagraph>();
        using var doc = JsonDocument.Parse(json);
        bool hasParagraphs = doc.RootElement.TryGetProperty("paragraphs", out var listEl) ||
                             doc.RootElement.TryGetProperty("Paragraphs", out listEl);
        if (!hasParagraphs || listEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var pEl in listEl.EnumerateArray())
        {
            int pid = 0;
            if (pEl.TryGetProperty("id", out var idProp) || pEl.TryGetProperty("Id", out idProp))
            {
                pid = idProp.GetInt32();
            }

            var morphemes = new List<SudachiMorpheme>();
            bool hasMorphemes = pEl.TryGetProperty("morphemes", out var mList) ||
                                pEl.TryGetProperty("Morphemes", out mList);
            if (hasMorphemes && mList.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mList.EnumerateArray())
                {
                    string surface = m.TryGetProperty("surface", out var sProp) ? sProp.GetString() ?? "" : "";
                    int start = m.TryGetProperty("start", out var stProp) ? stProp.GetInt32() : 0;
                    int end = m.TryGetProperty("end", out var endProp) ? endProp.GetInt32() : 0;
                    string lemma = m.TryGetProperty("lemma", out var lmProp) ? lmProp.GetString() ?? surface : surface;
                    string reading = m.TryGetProperty("reading", out var rdProp) ? rdProp.GetString() ?? surface : surface;
                    string wt = m.TryGetProperty("wordType", out var wtProp) ? wtProp.GetString() ?? "unknown" : "unknown";
                    string subType = m.TryGetProperty("subType", out var subProp) ? subProp.GetString() ?? "unknown" : "unknown";
                    string code = m.TryGetProperty("code", out var cdProp) ? cdProp.GetString() ?? "unk" : "unk";

                    var classes = new List<string>();
                    if (m.TryGetProperty("classes", out var clsArr) && clsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in clsArr.EnumerateArray())
                        {
                            string? s = c.GetString();
                            if (!string.IsNullOrEmpty(s)) classes.Add(s);
                        }
                    }

                    morphemes.Add(new SudachiMorpheme(surface, start, end, lemma, reading, wt, subType, code, classes));
                }
            }

            result[pid] = new SudachiParagraph(pid, morphemes);
        }

        return result;
    }

    public static IReadOnlyDictionary<int, SudachiParagraph> FallbackTagParagraphs(IReadOnlyList<(int Id, string Text)> paragraphs)
    {
        var result = new Dictionary<int, SudachiParagraph>(paragraphs.Count);
        foreach (var (id, text) in paragraphs)
        {
            var morphemes = new List<SudachiMorpheme>();
            if (string.IsNullOrWhiteSpace(text))
            {
                result[id] = new SudachiParagraph(id, morphemes);
                continue;
            }

            int idx = 0;
            while (idx < text.Length)
            {
                char c = text[idx];
                if (char.IsWhiteSpace(c))
                {
                    idx++;
                    continue;
                }

                int start = idx;
                bool isKana = c >= 0x3040 && c <= 0x30ff;
                bool isKanji = c >= 0x4e00 && c <= 0x9faf;

                if (isKanji)
                {
                    while (idx < text.Length && (text[idx] >= 0x4e00 && text[idx] <= 0x9faf)) idx++;
                    string surface = text[start..idx];
                    morphemes.Add(new SudachiMorpheme(surface, start, idx, surface, surface, "noun", "noun", "noun", ["noun", "ir-pos-noun"]));
                }
                else if (isKana)
                {
                    while (idx < text.Length && (text[idx] >= 0x3040 && text[idx] <= 0x30ff)) idx++;
                    string surface = text[start..idx];
                    string wt = surface.Length == 1 ? "particle" : "particle";
                    string st = surface.Length == 1 ? "particle-single" : "particle-multi";
                    string cd = surface.Length == 1 ? "prt-single" : "prt-multi";
                    morphemes.Add(new SudachiMorpheme(surface, start, idx, surface, surface, wt, st, cd, [wt, st, $"ir-pos-{cd}"]));
                }
                else
                {
                    while (idx < text.Length && !(text[idx] >= 0x3040 && text[idx] <= 0x30ff) && !(text[idx] >= 0x4e00 && text[idx] <= 0x9faf) && !char.IsWhiteSpace(text[idx])) idx++;
                    string surface = text[start..idx];
                    morphemes.Add(new SudachiMorpheme(surface, start, idx, surface, surface, "unknown", "unknown", "unk", []));
                }
            }

            result[id] = new SudachiParagraph(id, morphemes);
        }

        return result;
    }

    public static IReadOnlyDictionary<int, PosClassification> ParseClassificationsJson(string json)
    {
        var result = new Dictionary<int, PosClassification>();
        using var doc = JsonDocument.Parse(json);
        bool hasClassifications = doc.RootElement.TryGetProperty("classifications", out var listEl) ||
                                  doc.RootElement.TryGetProperty("Classifications", out listEl);
        if (!hasClassifications || listEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in listEl.EnumerateArray())
        {
            int id = 0;
            if (item.TryGetProperty("id", out var idProp) || item.TryGetProperty("Id", out idProp))
            {
                id = idProp.GetInt32();
            }

            string wt = "unknown";
            if (item.TryGetProperty("wordType", out var wtProp) || item.TryGetProperty("WordType", out wtProp))
            {
                wt = wtProp.GetString() ?? "unknown";
            }

            string st = "unknown";
            if (item.TryGetProperty("subType", out var stProp) || item.TryGetProperty("SubType", out stProp))
            {
                st = stProp.GetString() ?? "unknown";
            }

            string cd = "unk";
            if (item.TryGetProperty("code", out var cdProp) || item.TryGetProperty("Code", out cdProp))
            {
                cd = cdProp.GetString() ?? "unk";
            }

            var classes = new List<string>();
            if ((item.TryGetProperty("classes", out var clsArr) || item.TryGetProperty("Classes", out clsArr)) &&
                clsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in clsArr.EnumerateArray())
                {
                    string? s = c.GetString();
                    if (!string.IsNullOrEmpty(s)) classes.Add(s);
                }
            }

            result[id] = new PosClassification(id, wt, st, cd, classes);
        }

        return result;
    }
}

public sealed record SudachiMorpheme(
    string Surface,
    int Start,
    int End,
    string Lemma,
    string Reading,
    string WordType,
    string SubType,
    string Code,
    IReadOnlyList<string> Classes
);

public sealed record SudachiParagraph(
    int Id,
    IReadOnlyList<SudachiMorpheme> Morphemes
);
