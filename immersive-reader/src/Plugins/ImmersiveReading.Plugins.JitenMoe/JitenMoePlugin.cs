using System.Globalization;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.JitenMoe;

public sealed class JitenMoeSettings
{
    public string ApiKey { get; set; } = "";
    public int DefaultMediaType { get; set; } = 4;
}

/// <summary>
/// jiten.moe integration: deck search/detail/difficulty lookups, applying a deck's metadata
/// (link, difficulty, tags, synopsis) to local books, series-wide volume matching and
/// "1-click" deck.csv download. Everything the app used to do for jiten.moe in its core lives here;
/// the UI is contributed through <c>Assets/ui.js</c>.
/// </summary>
public sealed class JitenMoePlugin : IImmersivePlugin, IDisposable
{
    public const string PluginIdValue = "jitenMoe";

    /// <summary>Library custom-field keys (definitions are seeded by the library; values are written here).</summary>
    public const string UrlFieldKey = "jiten_moe_url";
    public const string DifficultyFieldKey = "jiten_difficulty";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdValue,
        Name = "jiten.moe Integration",
        Version = "1.0.0",
        Author = "RGnt",
        Description = "Match books and series against jiten.moe, import difficulty/tags/synopsis and download vocabulary decks.",
        ReloadBehavior = PluginReloadBehavior.Hot,
        UiEntry = "ui.js",
        StyleEntry = "ui.css"
    };

    private JitenMoeSettings _settings = new();
    private JitenMoeService _jiten = null!;
    private IHostLibrary _library = null!;
    private string _dataDir = "";
    private IDisposable? _configSub;

    public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        _library = context.Library;
        _dataDir = context.PluginDataDirectory;
        _settings = context.Config.Get<JitenMoeSettings>();
        _configSub = context.Config.OnConfigChanged<JitenMoeSettings>(s => _settings = s);
        _jiten = new JitenMoeService(
            httpClient: JitenMoeService.CreateHttpClient(),
            apiKeyProvider: new SettingsJitenApiKeyProvider(() => _settings.ApiKey));

        context.RegisterRpcAction("search", SearchAsync);
        context.RegisterRpcAction("getDeckDetail", GetDeckDetailAsync);
        context.RegisterRpcAction("getDeckDifficulty", async (p, c) => await _jiten.GetDifficultyAsync(DeckId(p), c));
        context.RegisterRpcAction("getDeckStats", async (p, c) => await _jiten.GetStatsAsync(DeckId(p), c));
        context.RegisterRpcAction("getVocabularyPreview", GetVocabularyPreviewAsync);
        context.RegisterRpcAction("applyMetadata", ApplyMetadata);
        context.RegisterRpcAction("matchSeries", MatchSeriesAsync);
        context.RegisterRpcAction("applySeriesMapping", ApplySeriesMapping);
        context.RegisterRpcAction("downloadDeckCsv", DownloadDeckCsvAsync);
        context.RegisterRpcAction("downloadSeriesDecksCsv", DownloadSeriesDecksCsvAsync);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        _configSub?.Dispose();
        _configSub = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => OnDisableAsync();

    // Registered in the DI container as a singleton: the container's synchronous Dispose() throws for
    // types that only implement IAsyncDisposable, so the plugin implements both (same as PosHighlighter).
    public void Dispose()
    {
        _configSub?.Dispose();
        _configSub = null;
    }

    // ---------------------------------------------------------------- lookups

    private async Task<object?> SearchAsync(JsonElement p, CancellationToken ct)
    {
        string query = RequiredString(p, "query");
        int? mediaType = OptionalInt(p, "mediaType");
        var (items, totalItems) = await _jiten
            .SearchAsync(query, mediaType, OptionalString(p, "sortBy"), OptionalString(p, "sortOrder"), ct)
            .ConfigureAwait(false);
        return new { items, totalItems };
    }

    private async Task<object?> GetDeckDetailAsync(JsonElement p, CancellationToken ct)
    {
        var detail = await _jiten.GetDetailAsync(DeckId(p), ct).ConfigureAwait(false);
        return new { mainDeck = detail.MainDeck, subDecks = detail.SubDecks };
    }

    private async Task<object?> GetVocabularyPreviewAsync(JsonElement p, CancellationToken ct)
    {
        long deckId = DeckId(p);
        var words = await _jiten.GetVocabularyPreviewAsync(deckId, OptionalInt(p, "limit") ?? 15, ct).ConfigureAwait(false);
        return new { deckId, words };
    }

    // ---------------------------------------------------------------- apply to library

    private Task<object?> ApplyMetadata(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        string jitenUrl = RequiredString(p, "jitenUrl");
        double? difficulty = OptionalDouble(p, "difficulty");

        _library.SetCustomFieldValue("book", bookId, UrlFieldKey, jitenUrl, null);
        if (difficulty.HasValue)
        {
            _library.SetCustomFieldValue("book", bookId, DifficultyFieldKey,
                difficulty.Value.ToString("F2", CultureInfo.InvariantCulture), difficulty.Value);
        }

        var tagIds = ResolveTagIds(ReadStrings(p, "tags"));
        string? description = OptionalString(p, "description");
        ApplyTagsAndSynopsis(bookId, tagIds, description, allowSynopsis: !string.IsNullOrWhiteSpace(description));

        return Task.FromResult<object?>(new { ok = true, bookId, jitenUrl, difficulty });
    }

    private Task<object?> ApplySeriesMapping(JsonElement p, CancellationToken ct)
    {
        if (!p.TryGetProperty("mappings", out var mapArr) || mapArr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("mappings array is required.");

        var tagIds = ResolveTagIds(ReadStrings(p, "tags"));
        bool updateSynopsis = OptionalBool(p, "updateSynopsis") ?? false;

        int updated = 0;
        foreach (var item in mapArr.EnumerateArray())
        {
            string bookId = OptionalString(item, "bookId") ?? "";
            if (string.IsNullOrWhiteSpace(bookId)) continue;

            string? url = OptionalString(item, "jitenUrl");
            double? diff = OptionalDouble(item, "difficulty");
            if (!string.IsNullOrWhiteSpace(url))
                _library.SetCustomFieldValue("book", bookId, UrlFieldKey, url, null);
            if (diff.HasValue)
                _library.SetCustomFieldValue("book", bookId, DifficultyFieldKey,
                    diff.Value.ToString("F2", CultureInfo.InvariantCulture), diff.Value);

            ApplyTagsAndSynopsis(bookId, tagIds, OptionalString(item, "description"), updateSynopsis);
            updated++;
        }

        return Task.FromResult<object?>(new { ok = true, updatedCount = updated });
    }

    private List<string> ResolveTagIds(IEnumerable<string> names)
    {
        var existing = _library.ListTags().ToDictionary(t => t.Name, t => t.TagId, StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (string name in names)
        {
            if (!existing.TryGetValue(name, out string? id))
            {
                id = _library.SaveTag(null, name, "#38bdf8", 0).TagId;
                existing[name] = id;
            }
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>Adds tags (union with the book's current tags) and fills an empty description.</summary>
    private void ApplyTagsAndSynopsis(string bookId, List<string> tagIds, string? description, bool allowSynopsis)
    {
        bool hasSynopsis = allowSynopsis && !string.IsNullOrWhiteSpace(description);
        if (tagIds.Count == 0 && !hasSynopsis) return;

        var detail = _library.GetBookDetail(bookId);
        if (detail is null) return;

        List<string>? finalTagIds = null;
        if (tagIds.Count > 0)
        {
            var current = detail.Summary.Tags.Select(t => t.TagId).ToHashSet(StringComparer.Ordinal);
            current.UnionWith(tagIds);
            finalTagIds = current.ToList();
        }

        string? newDescription = hasSynopsis && string.IsNullOrWhiteSpace(detail.Summary.Description)
            ? description
            : null;

        if (finalTagIds is not null || newDescription is not null)
        {
            _library.UpdateBookMetadata(new BookMetadataUpdateRequest(
                BookId: bookId, TagIds: finalTagIds, Description: newDescription));
        }
    }

    // ---------------------------------------------------------------- series matching

    private async Task<object?> MatchSeriesAsync(JsonElement p, CancellationToken ct)
    {
        string seriesName = RequiredString(p, "seriesName").Trim();
        long? requested = p.TryGetProperty("deckId", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null;

        long targetDeckId;
        if (requested is > 0)
        {
            targetDeckId = requested.Value;
        }
        else
        {
            var (items, _) = await _jiten.SearchAsync(seriesName, mediaType: 4, ct: ct).ConfigureAwait(false);
            if (items.Count == 0)
                (items, _) = await _jiten.SearchAsync(seriesName, ct: ct).ConfigureAwait(false);
            if (items.Count == 0)
                return new { found = false, message = $"No decks found on jiten.moe for series '{seriesName}'" };
            targetDeckId = items[0].DeckId;
        }

        var detail = await _jiten.GetDetailAsync(targetDeckId, ct).ConfigureAwait(false);
        var localBooks = _library.ListBooks(new BookListQuery(SeriesName: seriesName, Sort: "series_or_title", Limit: 1000)).Items;

        var mappings = localBooks.Select(b =>
        {
            var sub = JitenMoeService.FindMatchingSubDeck(detail.SubDecks, b.SeriesIndex ?? 1.0, b.Title);
            return new
            {
                bookId = b.BookId,
                title = b.Title,
                seriesIndex = b.SeriesIndex,
                folderPath = b.FolderPath,
                matchedDeckId = sub?.DeckId,
                matchedTitle = sub?.OriginalTitle,
                difficulty = sub?.Difficulty ?? detail.MainDeck?.Difficulty,
                characterCount = sub?.CharacterCount,
                jitenUrl = sub?.JitenUrl ?? (detail.MainDeck != null ? $"https://jiten.moe/decks/media/{detail.MainDeck.DeckId}" : null)
            };
        }).ToList();

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allDecks = detail.MainDeck is null ? detail.SubDecks : detail.SubDecks.Append(detail.MainDeck);
        foreach (var deck in allDecks)
        {
            foreach (var g in deck.Genres) tags.Add(g);
            foreach (var t in deck.Tags) tags.Add(t.Name);
        }

        return new
        {
            found = true,
            parentDeck = detail.MainDeck,
            subDecksCount = detail.SubDecks.Count,
            mappings,
            availableTags = tags.ToList()
        };
    }

    // ---------------------------------------------------------------- deck.csv download

    private async Task<object?> DownloadDeckCsvAsync(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        long deckId = DeckId(p);
        var book = _library.GetBookDetail(bookId) ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        byte[] csv = await _jiten.DownloadDeckCsvAsync(deckId, ct).ConfigureAwait(false);
        string dest = WriteDeckCsv(book.Summary.FolderPath, csv);
        return new
        {
            ok = true, bookId, deckId, destinationPath = dest, fileName = "deck.csv",
            hasDeckCsv = true, fileSizeBytes = csv.Length
        };
    }

    private async Task<object?> DownloadSeriesDecksCsvAsync(JsonElement p, CancellationToken ct)
    {
        if (!p.TryGetProperty("downloads", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("downloads array is required.");

        var results = new List<object>();
        int downloaded = 0;
        foreach (var item in arr.EnumerateArray())
        {
            string bookId = OptionalString(item, "bookId") ?? "";
            long deckId = item.TryGetProperty("deckId", out var di) && di.ValueKind == JsonValueKind.Number ? di.GetInt64() : 0;
            if (string.IsNullOrWhiteSpace(bookId) || deckId <= 0) continue;

            var book = _library.GetBookDetail(bookId);
            if (book is null) continue;

            try
            {
                byte[] csv = await _jiten.DownloadDeckCsvAsync(deckId, ct).ConfigureAwait(false);
                string dest = WriteDeckCsv(book.Summary.FolderPath, csv);
                results.Add(new { bookId, title = book.Summary.Title, deckId, savedPath = dest, fileSizeBytes = csv.Length });
                downloaded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new { bookId, title = book.Summary.Title, deckId, error = ex.Message });
            }
        }

        return new { ok = true, downloadedCount = downloaded, results };
    }

    private string WriteDeckCsv(string? bookFolder, byte[] csv)
    {
        string dir = !string.IsNullOrWhiteSpace(bookFolder) && Directory.Exists(bookFolder)
            ? bookFolder
            : Path.Combine(_dataDir, "downloads");
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, "deck.csv");
        File.WriteAllBytes(dest, csv);
        return dest;
    }

    // ---------------------------------------------------------------- payload helpers

    private static long DeckId(JsonElement p)
    {
        if (p.TryGetProperty("deckId", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number) return id.GetInt64();
            if (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out long parsed)) return parsed;
        }
        throw new ArgumentException("deckId is required.");
    }

    private static string RequiredString(JsonElement p, string name)
        => OptionalString(p, name) is { Length: > 0 } s ? s : throw new ArgumentException($"{name} is required.");

    private static string? OptionalString(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? OptionalInt(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? OptionalDouble(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? OptionalBool(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static IEnumerable<string> ReadStrings(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var e in arr.EnumerateArray())
        {
            string s = e.GetString()?.Trim() ?? "";
            if (s.Length > 0) yield return s;
        }
    }
}
