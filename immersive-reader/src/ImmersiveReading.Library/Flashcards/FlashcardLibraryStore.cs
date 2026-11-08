using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImmersiveReading.Library.Flashcards;

internal static class FlashcardLibraryStore
{
    public static JsonObject LoadOrCreateLibraryRoot(string libraryJsonPath)
    {
        if (File.Exists(libraryJsonPath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(libraryJsonPath, Encoding.UTF8));
                if (node is JsonObject obj)
                {
                    return obj;
                }
            }
            catch
            {
                // Fall through to fresh root
            }
        }

        return new JsonObject
        {
            ["schema_version"] = "1.0",
            ["books"] = new JsonArray()
        };
    }

    public static IReadOnlyList<FlashcardSummaryItem> ListBookCards(string libraryJsonPath, string bookId, int limit = 200)
    {
        var result = new List<FlashcardSummaryItem>();
        if (!File.Exists(libraryJsonPath))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(libraryJsonPath, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("books", out var booksArr) || booksArr.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var b in booksArr.EnumerateArray())
            {
                string? bid = b.TryGetProperty("id", out var idP) ? idP.GetString() : null;
                if (!string.Equals(bid, bookId, StringComparison.Ordinal)) continue;

                if (!b.TryGetProperty("chapters", out var chArr) || chArr.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                foreach (var ch in chArr.EnumerateArray())
                {
                    string chId = ch.TryGetProperty("id", out var cidP) ? (cidP.GetString() ?? "") : "";
                    int chNum = ch.TryGetProperty("number", out var cnumP) ? cnumP.GetInt32() : 0;
                    string chTitle = ch.TryGetProperty("title", out var ctitP) ? (ctitP.GetString() ?? "") : "";

                    if (!ch.TryGetProperty("cards", out var cardsArr) || cardsArr.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var card in cardsArr.EnumerateArray())
                    {
                        if (result.Count >= limit) return result;
                        result.Add(new FlashcardSummaryItem(
                            Id: card.TryGetProperty("id", out var cardIdP) ? (cardIdP.GetString() ?? "") : "",
                            Kind: card.TryGetProperty("kind", out var kP) ? (kP.GetString() ?? "vocabulary") : "vocabulary",
                            Prompt: card.TryGetProperty("prompt", out var pP) ? (pP.GetString() ?? "") : "",
                            Reading: card.TryGetProperty("reading", out var rP) ? (rP.GetString() ?? "") : "",
                            Meaning: card.TryGetProperty("meaning", out var mP) ? (mP.GetString() ?? "") : "",
                            Example: card.TryGetProperty("example", out var exP) ? (exP.GetString() ?? "") : "",
                            ChapterId: chId,
                            ChapterNumber: chNum,
                            ChapterTitle: chTitle,
                            SetNumber: card.TryGetProperty("set_number", out var snP) && snP.ValueKind == JsonValueKind.Number ? snP.GetInt32() : 0
                        ));
                    }
                }
                break;
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return result;
    }
}

