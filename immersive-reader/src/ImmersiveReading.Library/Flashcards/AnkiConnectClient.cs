using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImmersiveReading.Library.Flashcards;

public sealed record AnkiConnectNoteRequest(
    string Expression,
    string Reading,
    string Meaning,
    string Sentence,
    string? Source = null,
    IReadOnlyList<string>? Tags = null
);

public sealed record AnkiSyncResult(
    bool Success,
    int AddedCount,
    int DuplicateCount,
    int ErrorCount,
    string? Message = null,
    IReadOnlyList<long>? NoteIds = null
);

public sealed class AnkiConnectClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<(bool Reachable, int Version, string? Error)> PingAsync(string endpointUrl = "http://127.0.0.1:8765", CancellationToken ct = default)
    {
        try
        {
            var res = await InvokeAsync<int>(endpointUrl, "version", null, ct).ConfigureAwait(false);
            return (true, res, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>> GetDeckNamesAsync(string endpointUrl = "http://127.0.0.1:8765", CancellationToken ct = default)
    {
        return await InvokeAsync<List<string>>(endpointUrl, "deckNames", null, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<string>> GetModelNamesAsync(string endpointUrl = "http://127.0.0.1:8765", CancellationToken ct = default)
    {
        return await InvokeAsync<List<string>>(endpointUrl, "modelNames", null, ct).ConfigureAwait(false) ?? [];
    }

    public async Task<IReadOnlyList<string>> GetModelFieldNamesAsync(string endpointUrl, string modelName, CancellationToken ct = default)
    {
        var paramsObj = new { modelName };
        return await InvokeAsync<List<string>>(endpointUrl, "modelFieldNames", paramsObj, ct).ConfigureAwait(false) ?? [];
    }

    public async Task CreateDeckAsync(string endpointUrl, string deckName, CancellationToken ct = default)
    {
        try
        {
            await InvokeAsync<long>(endpointUrl, "createDeck", new { deck = deckName }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore if deck already exists or creation failed non-fatally
        }
    }

    public async Task<AnkiSyncResult> SyncNotesAsync(
        string endpointUrl,
        string deckName,
        string modelName,
        IReadOnlyList<AnkiConnectNoteRequest> notes,
        CancellationToken ct = default)
    {
        if (notes.Count == 0)
        {
            return new AnkiSyncResult(true, 0, 0, 0, "No notes provided.");
        }

        try
        {
            await CreateDeckAsync(endpointUrl, deckName, ct).ConfigureAwait(false);

            // Fetch available fields for target model to construct adaptive note payloads
            IReadOnlyList<string> modelFields = [];
            try
            {
                modelFields = await GetModelFieldNamesAsync(endpointUrl, modelName, ct).ConfigureAwait(false);
            }
            catch
            {
                // If model lookup fails, we fallback to default field sets
            }

            var notePayloads = new List<object>();
            foreach (var note in notes)
            {
                var fields = MapFields(note, modelFields);
                var noteObj = new
                {
                    deckName,
                    modelName,
                    fields,
                    options = new
                    {
                        allowDuplicate = false,
                        duplicateScope = "deck"
                    },
                    tags = (note.Tags != null && note.Tags.Count > 0)
                        ? note.Tags.ToArray()
                        : new[] { "ImmersiveReading" }
                };
                notePayloads.Add(noteObj);
            }

            // AnkiConnect canAddNotes check or addNotes call
            var addNotesParams = new { notes = notePayloads };
            var result = await InvokeAsync<JsonArray>(endpointUrl, "addNotes", addNotesParams, ct).ConfigureAwait(false);

            var noteIds = new List<long>();
            int added = 0;
            int duplicates = 0;
            int errors = 0;

            if (result != null)
            {
                foreach (var item in result)
                {
                    if (item == null)
                    {
                        // Anki returns null in the array if the note was duplicate / could not be added
                        duplicates++;
                    }
                    else if (item.AsValue().TryGetValue<long>(out long id))
                    {
                        noteIds.Add(id);
                        added++;
                    }
                    else
                    {
                        errors++;
                    }
                }
            }

            return new AnkiSyncResult(
                Success: true,
                AddedCount: added,
                DuplicateCount: duplicates,
                ErrorCount: errors,
                Message: $"Synced {added} card(s) to deck '{deckName}' ({duplicates} duplicates skipped).",
                NoteIds: noteIds
            );
        }
        catch (Exception ex)
        {
            return new AnkiSyncResult(
                Success: false,
                AddedCount: 0,
                DuplicateCount: 0,
                ErrorCount: notes.Count,
                Message: $"AnkiConnect error: {ex.Message}"
            );
        }
    }

    private static Dictionary<string, string> MapFields(AnkiConnectNoteRequest note, IReadOnlyList<string> modelFields)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (modelFields.Count == 0)
        {
            // Default generic fallback
            dict["Expression"] = note.Expression;
            dict["Reading"] = note.Reading;
            dict["Meaning"] = note.Meaning;
            dict["Sentence"] = note.Sentence;
            if (!string.IsNullOrEmpty(note.Source)) dict["Source"] = note.Source;
            return dict;
        }

        // If target model has "Front" and "Back" (Basic model)
        bool hasFront = modelFields.Any(f => f.Equals("Front", StringComparison.OrdinalIgnoreCase));
        bool hasBack = modelFields.Any(f => f.Equals("Back", StringComparison.OrdinalIgnoreCase));
        bool hasSpecificJapaneseFields = modelFields.Any(f =>
            f.Equals("Expression", StringComparison.OrdinalIgnoreCase) ||
            f.Equals("Reading", StringComparison.OrdinalIgnoreCase));

        if (hasFront && hasBack && !hasSpecificJapaneseFields)
        {
            string frontText = string.IsNullOrEmpty(note.Reading) || note.Reading == note.Expression
                ? $"{note.Expression}<br><br><span style='color:#888;'>{note.Sentence}</span>"
                : $"{note.Expression} [{note.Reading}]<br><br><span style='color:#888;'>{note.Sentence}</span>";
            dict["Front"] = frontText;
            dict["Back"] = note.Meaning;
            return dict;
        }

        foreach (var f in modelFields)
        {
            string lower = f.Trim().ToLowerInvariant();
            if (lower is "expression" or "word" or "kanji" or "vocabulary" or "target" or "headword" or "term")
            {
                dict[f] = note.Expression;
            }
            else if (lower is "reading" or "kana" or "furigana" or "hiragana" or "pronunciation")
            {
                dict[f] = note.Reading;
            }
            else if (lower is "meaning" or "glossary" or "definition" or "english" or "translation" or "back")
            {
                dict[f] = note.Meaning;
            }
            else if (lower is "sentence" or "example" or "context" or "sent" or "sentences")
            {
                dict[f] = note.Sentence;
            }
            else if (lower is "source" or "book" or "reference" or "origin")
            {
                dict[f] = note.Source ?? "";
            }
            else if (lower is "notes" or "comment" or "notes / tags")
            {
                dict[f] = $"Mined from {note.Source ?? "Immersive Reading"}";
            }
        }

        return dict;
    }

    private static async Task<T?> InvokeAsync<T>(string endpointUrl, string action, object? @params, CancellationToken ct)
    {
        var requestPayload = new
        {
            action,
            version = 6,
            @params
        };

        using var response = await HttpClient.PostAsJsonAsync(endpointUrl, requestPayload, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(JsonOpts, ct).ConfigureAwait(false);
        if (json == null) throw new InvalidOperationException("Empty response from AnkiConnect.");

        if (json.TryGetPropertyValue("error", out var errorNode) && errorNode != null && errorNode.GetValue<string?>() is { Length: > 0 } errMsg)
        {
            throw new InvalidOperationException($"AnkiConnect error: {errMsg}");
        }

        if (json.TryGetPropertyValue("result", out var resultNode) && resultNode != null)
        {
            return resultNode.Deserialize<T>(JsonOpts);
        }

        return default;
    }
}

