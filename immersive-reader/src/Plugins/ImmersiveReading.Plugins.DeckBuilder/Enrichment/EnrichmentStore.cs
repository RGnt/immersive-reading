using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImmersiveReading.Plugins.DeckBuilder.Enrichment;

/// <summary>Keeps a finished enrichment on disk so a restart (or a rebuild of the deck) never costs tokens again.</summary>
public sealed class EnrichmentStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private string PathFor(string bookId)
    {
        string safe = string.Concat(bookId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(directory, safe + ".json");
    }

    public void Save(string bookId, EnrichmentResult result)
    {
        Directory.CreateDirectory(directory);
        string path = PathFor(bookId);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Returns the stored result only if it was made for the same book file (<paramref name="bookStamp"/>).</summary>
    public EnrichmentResult? Load(string bookId, string bookStamp)
    {
        string path = PathFor(bookId);
        if (!File.Exists(path)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<EnrichmentResult>(File.ReadAllText(path), Json);
            return result is not null && result.BookStamp == bookStamp ? result : null;
        }
        catch (JsonException)
        {
            return null; // a corrupt cache file is simply an enrichment that has not been run
        }
    }
}

/// <summary>
/// Writes <c>deck-grammar.json</c>, the file next to <c>deck.csv</c> that the Python study pipeline reads (offline, as data).
/// Contract: specs/16-deck-llm-enrichment-spec.md.
/// </summary>
public static class DeckGrammarJson
{
    public const string Schema = "immersive-deck-grammar/1";
    public const string FileName = "deck-grammar.json";

    public static string Render(EnrichmentResult result, string bookTitle)
    {
        var root = new JsonObject
        {
            ["schema"] = Schema,
            // Examples are real book sentences; the grammar notes themselves are model output and unreviewed.
            ["status"] = "llm_generated_examples_text_verified",
            ["book_title"] = bookTitle,
            ["generator"] = new JsonObject
            {
                ["plugin"] = "deckBuilder",
                ["model"] = result.Model,
                ["generated_at"] = result.GeneratedAtUtc.ToString("O")
            },
            ["grammar"] = new JsonArray(result.Points.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id,
                ["pattern"] = p.Pattern,
                ["category"] = p.Category,
                ["meaning"] = p.Meaning,
                ["explanation"] = p.Explanation,
                ["first_chapter_index"] = p.FirstChapterIndex,
                ["first_chapter_title"] = p.FirstChapterTitle,
                ["examples"] = new JsonArray(p.Examples.Select(e => (JsonNode)new JsonObject
                {
                    ["text"] = e.Text,
                    ["surface"] = e.Surface,
                    ["chapter_index"] = e.ChapterIndex
                }).ToArray())
            }).ToArray()),
            ["function_word_notes"] = new JsonObject(result.FunctionUses.Select(kv =>
                KeyValuePair.Create<string, JsonNode?>(kv.Key, new JsonArray(kv.Value.Select(u => (JsonNode)new JsonObject
                {
                    ["function"] = u.Function,
                    ["note"] = u.Note,
                    ["example"] = u.Example
                }).ToArray()))))
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
