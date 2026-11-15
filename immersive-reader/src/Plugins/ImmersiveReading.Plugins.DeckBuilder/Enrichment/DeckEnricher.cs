using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImmersiveReading.Plugins.DeckBuilder.Enrichment;

/// <summary>
/// Asks an LLM for the grammar used in each chapter and for how each particle / auxiliary is used, then keeps only what
/// can be checked against the book: every example must name a sentence of the chunk and a <c>surface</c> that occurs
/// verbatim in that sentence. The model never writes sentences of its own, so the study material cannot contain an
/// invented quote, and story sentences are never translated (English is limited to meaning / explanation / notes).
/// </summary>
public sealed class DeckEnricher(IDeckLlm llm, DeckEnrichmentOptions? options = null)
{
    private readonly DeckEnrichmentOptions _options = options ?? new DeckEnrichmentOptions();

    /// <summary>Keeps Japanese readable in the prompt (the default encoder would write every kana as a \uXXXX escape).</summary>
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal) { "grammar", "expression", "conjugation" };

    private const string SystemPrompt = """
        You are a Japanese grammar analyst preparing study material for ONE specific book. You receive a chunk of the book as numbered sentences and answer with a single JSON object, nothing else.

        RULES
        1. Never translate or paraphrase the story sentences. Refer to them only by sentence id and by `surface`, an exact substring copied character for character from that sentence. English is allowed only in `meaning`, `explanation`, `function` and `note`.
        2. grammarPoints: grammar a learner must understand to read THIS chunk: verb/adjective forms and their compounds (te-form patterns, passive, causative, conditional, volitional...), sentence-final expressions, conjunctions, set phrases and idioms. Do NOT list vocabulary, names, or single particles (particles go to particleNotes). Write `pattern` in standard textbook notation, e.g. 〜てしまう, 〜ながら, 〜かもしれない. `category` is one of: grammar, expression, conjugation.
        3. Each grammar point needs 1 to 3 `examples`: {"sentenceId": <id>, "surface": "<exact text in that sentence showing the pattern>"}.
        4. If a pattern is in KNOWN_PATTERNS, reuse the identical `pattern` text and add only NEW examples (omit meaning and explanation). Do not repeat a known pattern that has no new example in this chunk.
        5. particleNotes: for each word in FUNCTION_WORDS that occurs in this chunk, list each distinct function it has here (for に: destination, time, purpose, passive agent, ...), each with a short English `note` and one example {"sentenceId", "surface"}. Only functions that really occur in this chunk. Omit words that do not occur.
        6. Quality over quantity: return at most the number of grammarPoints you are given as MAX_POINTS, fewer if the chunk has little. When unsure, leave it out.

        JSON SHAPE
        {"grammarPoints":[{"pattern":"","category":"grammar","meaning":"","explanation":"","examples":[{"sentenceId":1,"surface":""}]}],
         "particleNotes":[{"word":"に","uses":[{"function":"","note":"","example":{"sentenceId":1,"surface":""}}]}]}
        """;

    /// <summary>Splits a chapter into chunks that each stay below <see cref="DeckEnrichmentOptions.MaxChunkChars"/>.</summary>
    public IReadOnlyList<ChapterSentences> Chunk(IReadOnlyList<ChapterSentences> chapters)
    {
        var chunks = new List<ChapterSentences>();
        foreach (var chapter in chapters)
        {
            var current = new List<string>();
            int size = 0;
            var parts = new List<List<string>>();
            foreach (string sentence in chapter.Sentences)
            {
                if (size + sentence.Length > _options.MaxChunkChars && current.Count > 0)
                {
                    parts.Add(current);
                    current = [];
                    size = 0;
                }
                current.Add(sentence);
                size += sentence.Length + 6; // "[123] " prefix
            }
            if (current.Count > 0) parts.Add(current);

            for (int i = 0; i < parts.Count; i++)
            {
                string title = parts.Count > 1 ? $"{chapter.Title} (part {i + 1}/{parts.Count})" : chapter.Title;
                chunks.Add(new ChapterSentences(chapter.ChapterIndex, title, parts[i]));
            }
        }
        return chunks;
    }

    public async Task<EnrichmentResult> RunAsync(
        IReadOnlyList<ChapterSentences> chapters,
        IReadOnlyList<string> functionWords,
        Action<EnrichmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        var chunks = Chunk(chapters.Where(c => c.Sentences.Count > 0).ToList());
        var result = new EnrichmentResult { Model = llm.ModelId, ChunksTotal = chunks.Count };
        var points = new Dictionary<string, GrammarPoint>(StringComparer.Ordinal);
        var functionSet = new HashSet<string>(functionWords, StringComparer.Ordinal);

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            progress?.Invoke(Snapshot(i, chunks.Count, chunk.Title, points, result));

            try
            {
                using var doc = await llm.CompleteJsonAsync(SystemPrompt, BuildUserPrompt(chunk, points.Values, functionWords), ct).ConfigureAwait(false);
                int accepted = Merge(doc.RootElement, chunk, points, functionSet, result);
                if (accepted == 0)
                {
                    // A valid answer that gave us nothing must not look like success: say so (and why, when the answer shows it).
                    result.Warnings.Add($"{chunk.Title}: the model answered, but nothing in the answer could be used"
                        + $" ({DescribeAnswer(doc.RootElement)}).");
                }
            }
            catch (DeckLlmException ex) when (!ex.Data.Contains("fatal"))
            {
                result.ChunksFailed++;
                result.Warnings.Add($"{chunk.Title}: {ex.Message}");
            }
        }

        progress?.Invoke(Snapshot(chunks.Count, chunks.Count, "", points, result));
        result.Points = points.Values.OrderBy(p => p.FirstChapterIndex).ThenBy(p => p.Pattern, StringComparer.Ordinal).ToList();
        result.GeneratedAtUtc = DateTime.UtcNow;
        return result;
    }

    private static EnrichmentProgress Snapshot(int done, int total, string chapter, Dictionary<string, GrammarPoint> points, EnrichmentResult result)
        => new(done, total, chapter, points.Count, result.FunctionUses.Count, result.ChunksFailed, result.Warnings.Count,
            result.Warnings.Skip(Math.Max(0, result.Warnings.Count - 5)).ToList());

    /// <summary>What the model's answer contained, for the warning: "grammarPoints: 3, particleNotes: 0" or "no grammarPoints / particleNotes".</summary>
    private static string DescribeAnswer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return "the answer is not a JSON object";
        string Count(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength().ToString() : "missing";
        return $"grammarPoints: {Count("grammarPoints")}, particleNotes: {Count("particleNotes")}";
    }

    private string BuildUserPrompt(ChapterSentences chunk, IEnumerable<GrammarPoint> known, IReadOnlyList<string> functionWords)
    {
        var sb = new StringBuilder();
        sb.Append("MAX_POINTS: ").Append(_options.MaxPointsPerChunk).Append('\n');
        sb.Append("CHAPTER: ").Append(chunk.Title).Append('\n');
        sb.Append("KNOWN_PATTERNS: ").Append(JsonSerializer.Serialize(known.Select(p => p.Pattern), PromptJson)).Append('\n');
        sb.Append("FUNCTION_WORDS: ").Append(JsonSerializer.Serialize(functionWords, PromptJson)).Append('\n');
        sb.Append("SENTENCES:\n");
        for (int i = 0; i < chunk.Sentences.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").Append(chunk.Sentences[i]).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- validation + merge

    /// <summary>Applies one answer. Returns how many things it contributed (new points, new examples, new particle uses).</summary>
    private int Merge(JsonElement root, ChapterSentences chunk, Dictionary<string, GrammarPoint> points,
        HashSet<string> functionWords, EnrichmentResult result)
    {
        int accepted = 0;
        if (root.TryGetProperty("grammarPoints", out var gp) && gp.ValueKind == JsonValueKind.Array)
        {
            int taken = 0;
            foreach (var item in gp.EnumerateArray())
            {
                if (taken >= _options.MaxPointsPerChunk) break;
                if (item.ValueKind != JsonValueKind.Object) continue;

                string pattern = Clip(Str(item, "pattern"), 60);
                if (pattern.Length == 0) continue;

                var examples = ReadExamples(item, chunk).ToList();
                if (examples.Count == 0)
                {
                    result.Warnings.Add($"Dropped '{pattern}' ({chunk.Title}): no example could be verified in the book text.");
                    continue;
                }

                string key = NormalizePattern(pattern);
                if (key.Length == 0) continue;
                if (!points.TryGetValue(key, out var point))
                {
                    string category = Str(item, "category").ToLowerInvariant();
                    point = new GrammarPoint
                    {
                        Id = "dg-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant(),
                        Pattern = pattern,
                        Category = Categories.Contains(category) ? category : "grammar",
                        FirstChapterIndex = chunk.ChapterIndex,
                        FirstChapterTitle = chunk.Title
                    };
                    points[key] = point;
                    taken++;
                    accepted++;
                }

                if (point.Meaning.Length == 0) point.Meaning = Clip(Str(item, "meaning"), 300);
                if (point.Explanation.Length == 0) point.Explanation = Clip(Str(item, "explanation"), 900);
                foreach (var ex in examples)
                {
                    if (point.Examples.Count >= 5) break;
                    if (point.Examples.All(e => e.Text != ex.Text)) { point.Examples.Add(ex); accepted++; }
                }
            }
        }

        if (root.TryGetProperty("particleNotes", out var pn) && pn.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pn.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string word = Str(item, "word");
                if (!functionWords.Contains(word)) continue;
                if (!item.TryGetProperty("uses", out var uses) || uses.ValueKind != JsonValueKind.Array) continue;

                if (!result.FunctionUses.TryGetValue(word, out var list)) result.FunctionUses[word] = list = [];
                foreach (var use in uses.EnumerateArray())
                {
                    if (use.ValueKind != JsonValueKind.Object || list.Count >= 6) continue;
                    string function = Clip(Str(use, "function"), 80);
                    string note = Clip(Str(use, "note"), 300);
                    if (function.Length == 0) continue;
                    if (list.Any(u => string.Equals(u.Function, function, StringComparison.OrdinalIgnoreCase))) continue;

                    // The example is optional for a particle note but, when given, it must be verifiable.
                    string? exampleText = null;
                    if (use.TryGetProperty("example", out var ex) && TryVerify(ex, chunk, out var verified))
                        exampleText = verified!.Text;
                    list.Add(new FunctionUse(function, note, exampleText));
                    accepted++;
                }
            }
        }
        return accepted;
    }

    private static IEnumerable<GrammarExample> ReadExamples(JsonElement item, ChapterSentences chunk)
    {
        if (!item.TryGetProperty("examples", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in arr.EnumerateArray())
        {
            if (TryVerify(e, chunk, out var example)) yield return example!;
        }
    }

    /// <summary>The example is real only if the sentence id exists in the chunk and the surface occurs in that sentence.</summary>
    private static bool TryVerify(JsonElement e, ChapterSentences chunk, out GrammarExample? example)
    {
        example = null;
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty("sentenceId", out var idProp)) return false;
        int id = idProp.ValueKind switch
        {
            JsonValueKind.Number when idProp.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(idProp.GetString(), out int n) => n,
            _ => 0
        };
        if (id < 1 || id > chunk.Sentences.Count) return false;

        string surface = Str(e, "surface").Trim();
        if (surface.Length == 0 || surface.Length > 60) return false;
        string sentence = chunk.Sentences[id - 1];
        if (!sentence.Contains(surface, StringComparison.Ordinal)) return false;

        example = new GrammarExample(sentence, surface, chunk.ChapterIndex);
        return true;
    }

    internal static string NormalizePattern(string pattern)
    {
        string s = pattern.Normalize(NormalizationForm.FormKC);
        s = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return s.TrimStart('~', '〜', '～', '…');
    }

    private static string Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>Joins a function word's uses into the single-cell <c>UsageNote</c> of deck.csv.</summary>
    public static string ToUsageNote(IReadOnlyList<FunctionUse> uses)
        => string.Join(" ; ", uses.Select(u => u.Note.Length > 0 ? $"{u.Function}: {u.Note}" : u.Function));
}
