using System.Globalization;
using System.Text.Json;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class JitenRpcModule(LibraryManager library, JitenMoeService? jitenMoe = null) : IRpcModule
{
    private readonly JitenMoeService _jitenMoe = jitenMoe ?? new();

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("library.searchJitenMoe", SearchJitenMoeAsync);
            yield return ("library.getJitenDeckDetail", GetJitenDeckDetailAsync);
            yield return ("library.getJitenDeckDifficulty", GetJitenDeckDifficultyAsync);
            yield return ("library.getJitenDeckStats", GetJitenDeckStatsAsync);
            yield return ("library.getJitenVocabularyPreview", GetJitenVocabularyPreviewAsync);
            yield return ("library.applyJitenMetadata", ApplyJitenMetadataAsync);
            yield return ("library.getSeriesVolumes", GetSeriesVolumesAsync);
            yield return ("library.matchSeriesOnJiten", MatchSeriesOnJitenAsync);
            yield return ("library.applySeriesJitenMapping", ApplySeriesJitenMappingAsync);
            yield return ("library.downloadJitenDeckCsv", DownloadJitenDeckCsvAsync);
            yield return ("library.downloadSeriesDecksCsv", DownloadSeriesDecksCsvAsync);
        }
    }

    private async Task<object?> SearchJitenMoeAsync(RpcRequest req, CancellationToken ct)
    {
        string query = req.RequiredString("query");
        int? mediaType = req.OptionalInt("mediaType");
        string? sortBy = req.OptionalString("sortBy");
        string? sortOrder = req.OptionalString("sortOrder");

        var (items, totalItems) = await _jitenMoe.SearchAsync(query, mediaType, sortBy, sortOrder, ct).ConfigureAwait(false);
        return new { items, totalItems };
    }

    private async Task<object?> GetJitenDeckDetailAsync(RpcRequest req, CancellationToken ct)
    {
        long deckId = GetDeckId(req);
        var detail = await _jitenMoe.GetDetailAsync(deckId, ct).ConfigureAwait(false);
        return new { mainDeck = detail.MainDeck, subDecks = detail.SubDecks };
    }

    private async Task<object?> GetJitenDeckDifficultyAsync(RpcRequest req, CancellationToken ct)
    {
        long deckId = GetDeckId(req);
        return await _jitenMoe.GetDifficultyAsync(deckId, ct).ConfigureAwait(false);
    }

    private async Task<object?> GetJitenDeckStatsAsync(RpcRequest req, CancellationToken ct)
    {
        long deckId = GetDeckId(req);
        return await _jitenMoe.GetStatsAsync(deckId, ct).ConfigureAwait(false);
    }

    private async Task<object?> GetJitenVocabularyPreviewAsync(RpcRequest req, CancellationToken ct)
    {
        long deckId = GetDeckId(req);
        int limit = req.OptionalInt("limit") ?? 15;
        var words = await _jitenMoe.GetVocabularyPreviewAsync(deckId, limit, ct).ConfigureAwait(false);
        return new { deckId, words };
    }

    private Task<object?> ApplyJitenMetadataAsync(RpcRequest req, CancellationToken ct)
    {
        string bookId = req.RequiredString("bookId");
        string jitenUrl = req.RequiredString("jitenUrl");
        double? difficulty = req.OptionalDouble("difficulty");
        string? description = req.OptionalString("description");

        library.SetCustomFieldValue("book", bookId, "jiten_moe_url", jitenUrl, null);
        if (difficulty.HasValue)
        {
            library.SetCustomFieldValue(
                "book",
                bookId,
                "jiten_difficulty",
                difficulty.Value.ToString("F2", CultureInfo.InvariantCulture),
                difficulty.Value);
        }

        List<string>? finalTagIds = null;
        if (req.Params.TryGetProperty("tags", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
        {
            var existingTags = library.ListTags().ToDictionary(t => t.Name, t => t.TagId, StringComparer.OrdinalIgnoreCase);
            var addedTagIds = new List<string>();

            foreach (var tElem in tagsArr.EnumerateArray())
            {
                string tagName = tElem.GetString()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(tagName)) continue;

                if (existingTags.TryGetValue(tagName, out string? existingTagId))
                {
                    addedTagIds.Add(existingTagId);
                }
                else
                {
                    var newTag = library.SaveTag(null, tagName, "#38bdf8", 0);
                    existingTags[tagName] = newTag.TagId;
                    addedTagIds.Add(newTag.TagId);
                }
            }

            if (addedTagIds.Count > 0)
            {
                var bookDetail = library.GetBookDetail(bookId);
                if (bookDetail is not null)
                {
                    var currentTagIds = bookDetail.Summary.Tags.Select(t => t.TagId).ToHashSet(StringComparer.Ordinal);
                    currentTagIds.UnionWith(addedTagIds);
                    finalTagIds = currentTagIds.ToList();
                }
            }
        }

        string? newDesc = null;
        if (!string.IsNullOrWhiteSpace(description))
        {
            var bookDetail = library.GetBookDetail(bookId);
            if (bookDetail is not null && string.IsNullOrWhiteSpace(bookDetail.Summary.Description))
            {
                newDesc = description;
            }
        }

        if (finalTagIds is not null || newDesc is not null)
        {
            library.UpdateBookMetadata(new BookMetadataUpdateRequest(
                BookId: bookId,
                TagIds: finalTagIds,
                Description: newDesc));
        }

        return Task.FromResult<object?>(new { ok = true, bookId, jitenUrl, difficulty });
    }

    private Task<object?> GetSeriesVolumesAsync(RpcRequest req, CancellationToken ct)
    {
        string seriesName = req.RequiredString("seriesName").Trim();
        string? folderId = req.OptionalString("folderId");

        var query = new BookListQuery(
            FolderId: folderId,
            IncludeDescendants: true,
            SeriesName: seriesName,
            Sort: "series_or_title",
            Limit: 1000);
        var res = library.ListBooks(query);
        return Task.FromResult<object?>(new { seriesName, items = res.Items, totalCount = res.TotalCount });
    }

    private async Task<object?> MatchSeriesOnJitenAsync(RpcRequest req, CancellationToken ct)
    {
        string seriesName = req.RequiredString("seriesName").Trim();
        long? requestedDeckId = req.Params.TryGetProperty("deckId", out var dProp) && dProp.ValueKind == JsonValueKind.Number
            ? dProp.GetInt64()
            : null;

        long targetDeckId;
        if (requestedDeckId.HasValue && requestedDeckId.Value > 0)
        {
            targetDeckId = requestedDeckId.Value;
        }
        else
        {
            var (searchItems, _) = await _jitenMoe.SearchAsync(seriesName, mediaType: 4, ct: ct).ConfigureAwait(false);
            if (searchItems.Count == 0)
            {
                (searchItems, _) = await _jitenMoe.SearchAsync(seriesName, ct: ct).ConfigureAwait(false);
            }
            if (searchItems.Count == 0)
            {
                return new { found = false, message = $"No decks found on jiten.moe for series '{seriesName}'" };
            }
            targetDeckId = searchItems[0].DeckId;
        }

        var detail = await _jitenMoe.GetDetailAsync(targetDeckId, ct).ConfigureAwait(false);
        var localBooks = library.ListBooks(new BookListQuery(SeriesName: seriesName, Sort: "series_or_title", Limit: 1000)).Items;

        var mappings = new List<object>();
        foreach (var b in localBooks)
        {
            double volIdx = b.SeriesIndex ?? 1.0;
            var matchedSub = JitenMoeService.FindMatchingSubDeck(detail.SubDecks, volIdx, b.Title);
            mappings.Add(new
            {
                bookId = b.BookId,
                title = b.Title,
                seriesIndex = b.SeriesIndex,
                folderPath = b.FolderPath,
                matchedDeckId = matchedSub?.DeckId,
                matchedTitle = matchedSub?.OriginalTitle,
                difficulty = matchedSub?.Difficulty ?? detail.MainDeck?.Difficulty,
                characterCount = matchedSub?.CharacterCount,
                jitenUrl = matchedSub?.JitenUrl ?? (detail.MainDeck != null ? $"https://jiten.moe/decks/media/{detail.MainDeck.DeckId}" : null)
            });
        }

        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (detail.MainDeck != null)
        {
            foreach (var g in detail.MainDeck.Genres) tagSet.Add(g);
            foreach (var t in detail.MainDeck.Tags) tagSet.Add(t.Name);
        }
        foreach (var s in detail.SubDecks)
        {
            foreach (var g in s.Genres) tagSet.Add(g);
            foreach (var t in s.Tags) tagSet.Add(t.Name);
        }

        return new
        {
            found = true,
            parentDeck = detail.MainDeck,
            subDecksCount = detail.SubDecks.Count,
            mappings,
            availableTags = tagSet.ToList()
        };
    }

    private Task<object?> ApplySeriesJitenMappingAsync(RpcRequest req, CancellationToken ct)
    {
        if (!req.Params.TryGetProperty("mappings", out var mapArr) || mapArr.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("mappings array is required.");
        }

        var tagsList = new List<string>();
        if (req.Params.TryGetProperty("tags", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tElem in tagsArr.EnumerateArray())
            {
                string tn = tElem.GetString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(tn)) tagsList.Add(tn);
            }
        }

        bool updateSynopsis = req.OptionalBool("updateSynopsis") ?? false;

        var existingTags = library.ListTags().ToDictionary(t => t.Name, t => t.TagId, StringComparer.OrdinalIgnoreCase);
        var targetTagIds = new List<string>();
        foreach (string tn in tagsList)
        {
            if (existingTags.TryGetValue(tn, out string? tid))
            {
                targetTagIds.Add(tid);
            }
            else
            {
                var newTag = library.SaveTag(null, tn, "#38bdf8", 0);
                existingTags[tn] = newTag.TagId;
                targetTagIds.Add(newTag.TagId);
            }
        }

        int updatedCount = 0;
        foreach (var item in mapArr.EnumerateArray())
        {
            string bookId = item.TryGetProperty("bookId", out var bi) ? bi.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(bookId)) continue;

            string? jitenUrl = item.TryGetProperty("jitenUrl", out var ju) ? ju.GetString() : null;
            double? diff = item.TryGetProperty("difficulty", out var df) && df.ValueKind == JsonValueKind.Number ? df.GetDouble() : null;
            string? desc = item.TryGetProperty("description", out var ds) ? ds.GetString() : null;

            if (!string.IsNullOrWhiteSpace(jitenUrl))
            {
                library.SetCustomFieldValue("book", bookId, "jiten_moe_url", jitenUrl, null);
            }
            if (diff.HasValue)
            {
                library.SetCustomFieldValue("book", bookId, "jiten_difficulty", diff.Value.ToString("F2", CultureInfo.InvariantCulture), diff.Value);
            }

            var bookDetail = library.GetBookDetail(bookId);
            if (bookDetail is not null)
            {
                List<string>? finalTagIds = null;
                if (targetTagIds.Count > 0)
                {
                    var currentIds = bookDetail.Summary.Tags.Select(t => t.TagId).ToHashSet(StringComparer.Ordinal);
                    currentIds.UnionWith(targetTagIds);
                    finalTagIds = currentIds.ToList();
                }

                string? newDesc = null;
                if (updateSynopsis && !string.IsNullOrWhiteSpace(desc) && string.IsNullOrWhiteSpace(bookDetail.Summary.Description))
                {
                    newDesc = desc;
                }

                if (finalTagIds is not null || newDesc is not null)
                {
                    library.UpdateBookMetadata(new BookMetadataUpdateRequest(
                        BookId: bookId,
                        TagIds: finalTagIds,
                        Description: newDesc));
                }
            }

            updatedCount++;
        }

        return Task.FromResult<object?>(new { ok = true, updatedCount });
    }

    private async Task<object?> DownloadJitenDeckCsvAsync(RpcRequest req, CancellationToken ct)
    {
        string bookId = req.RequiredString("bookId");
        long deckId = GetDeckId(req);

        var bookDetail = library.GetBookDetail(bookId)
            ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        string targetDir = bookDetail.Summary.FolderPath;
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = Path.Combine(AppContext.BaseDirectory, "downloads");
            Directory.CreateDirectory(targetDir);
        }

        byte[] csvBytes = await _jitenMoe.DownloadDeckCsvAsync(deckId, ct).ConfigureAwait(false);

        string destFile = Path.Combine(targetDir, "deck.csv");
        await File.WriteAllBytesAsync(destFile, csvBytes, ct).ConfigureAwait(false);

        return new
        {
            ok = true,
            bookId,
            deckId,
            destinationPath = destFile,
            fileName = "deck.csv",
            hasDeckCsv = true,
            fileSizeBytes = csvBytes.Length
        };
    }

    private async Task<object?> DownloadSeriesDecksCsvAsync(RpcRequest req, CancellationToken ct)
    {
        if (!req.Params.TryGetProperty("downloads", out var dlArr) || dlArr.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("downloads array is required.");
        }

        var results = new List<object>();
        foreach (var item in dlArr.EnumerateArray())
        {
            string bookId = item.TryGetProperty("bookId", out var bi) ? bi.GetString() ?? "" : "";
            long deckId = item.TryGetProperty("deckId", out var di) && di.ValueKind == JsonValueKind.Number ? di.GetInt64() : 0;
            if (string.IsNullOrWhiteSpace(bookId) || deckId <= 0) continue;

            var bookDetail = library.GetBookDetail(bookId);
            if (bookDetail is null) continue;

            string targetDir = bookDetail.Summary.FolderPath;
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = Path.Combine(AppContext.BaseDirectory, "downloads");
                Directory.CreateDirectory(targetDir);
            }

            try
            {
                byte[] csvBytes = await _jitenMoe.DownloadDeckCsvAsync(deckId, ct).ConfigureAwait(false);
                string destFile = Path.Combine(targetDir, "deck.csv");
                await File.WriteAllBytesAsync(destFile, csvBytes, ct).ConfigureAwait(false);

                results.Add(new
                {
                    bookId,
                    title = bookDetail.Summary.Title,
                    deckId,
                    savedPath = destFile,
                    fileSizeBytes = csvBytes.Length
                });
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    bookId,
                    title = bookDetail.Summary.Title,
                    deckId,
                    error = ex.Message
                });
            }
        }

        return new { ok = true, results };
    }

    private static long GetDeckId(RpcRequest req)
    {
        if (req.Params.TryGetProperty("deckId", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
        {
            return idProp.GetInt64();
        }
        return long.Parse(req.RequiredString("deckId"));
    }
}

