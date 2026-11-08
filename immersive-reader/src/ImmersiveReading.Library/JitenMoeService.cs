using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Library.Jiten;

namespace ImmersiveReading.Library;

public class JitenMoeService
{
    private const string DefaultBaseApiUrl = "https://api.jiten.moe/api";
    private const string UserAgentHeader = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 ImmersiveReading/1.0";

    private readonly HttpClient _httpClient;
    private readonly IJitenApiKeyProvider _apiKeyProvider;
    private readonly string _baseApiUrl;

    public JitenMoeService(
        HttpClient? httpClient = null,
        IJitenApiKeyProvider? apiKeyProvider = null,
        string baseApiUrl = DefaultBaseApiUrl)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _apiKeyProvider = apiKeyProvider ?? EnvJitenApiKeyProvider.Instance;
        _baseApiUrl = baseApiUrl.TrimEnd('/');
    }

    public static string? ResolveApiKey() => EnvJitenApiKeyProvider.Instance.GetApiKey();

    public static void SetApiKey(string? key) => EnvJitenApiKeyProvider.Instance.SetApiKey(key);

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentHeader);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        string? apiKey = ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return client;
    }

    public static string GetMediaTypeName(int mediaType) => JitenResponseParser.GetMediaTypeName(mediaType);
    public static string GetGenreName(int genre) => JitenResponseParser.GetGenreName(genre);
    public static string GetLinkTypeName(int linkType) => JitenResponseParser.GetLinkTypeName(linkType);
    public static JitenDeckSummary ParseDeck(JsonElement elem) => JitenResponseParser.ParseDeck(elem);
    public static JitenDifficultyResult ParseDifficulty(long deckId, JsonElement root) => JitenResponseParser.ParseDifficulty(deckId, root);
    public static JitenDeckStatsResult ParseStats(long deckId, JsonElement root) => JitenResponseParser.ParseStats(deckId, root);
    public static IReadOnlyList<JitenVocabularyPreviewWord> ParseVocabularyPreview(JsonElement root) => JitenResponseParser.ParseVocabularyPreview(root);
    public static JitenDeckSummary? FindMatchingSubDeck(IReadOnlyList<JitenDeckSummary> subDecks, double volumeIndex, string? bookTitle) =>
        JitenResponseParser.FindMatchingSubDeck(subDecks, volumeIndex, bookTitle);

    private HttpRequestMessage CreateRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.UserAgent.ParseAdd(UserAgentHeader);
        req.Headers.Accept.ParseAdd("application/json, text/plain, */*");
        string? apiKey = _apiKeyProvider.GetApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Add("X-Api-Key", apiKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        return req;
    }

    public async Task<(IReadOnlyList<JitenDeckSummary> items, int totalItems)> SearchAsync(
        string query,
        int? mediaType = null,
        string? sortBy = null,
        string? sortOrder = null,
        CancellationToken ct = default)
    {
        string cleaned = query.Replace('\u3000', ' ').Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return (Array.Empty<JitenDeckSummary>(), 0);
        }

        var sb = new StringBuilder($"{_baseApiUrl}/media-deck/get-media-decks?titleFilter={Uri.EscapeDataString(cleaned)}");
        if (mediaType.HasValue && mediaType.Value > 0)
        {
            sb.Append($"&mediaType={mediaType.Value}");
        }
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            sb.Append($"&sortBy={Uri.EscapeDataString(sortBy)}");
        }
        if (!string.IsNullOrWhiteSpace(sortOrder))
        {
            sb.Append($"&sortOrder={Uri.EscapeDataString(sortOrder)}");
        }

        using var req = CreateRequest(HttpMethod.Get, sb.ToString());
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var items = new List<JitenDeckSummary>();
        if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var elem in dataArr.EnumerateArray())
            {
                items.Add(ParseDeck(elem));
            }
        }

        int totalItems = doc.RootElement.TryGetProperty("totalItems", out var ti) ? ti.GetInt32() : items.Count;
        return (items, totalItems);
    }

    public async Task<JitenDeckDetailResult> GetDetailAsync(long deckId, CancellationToken ct = default)
    {
        string url = $"{_baseApiUrl}/media-deck/{deckId}/detail";
        using var req = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        JitenDeckSummary? mainDeck = null;
        var subDecks = new List<JitenDeckSummary>();

        if (doc.RootElement.TryGetProperty("data", out var dataObj) && dataObj.ValueKind == JsonValueKind.Object)
        {
            if (dataObj.TryGetProperty("mainDeck", out var md) && md.ValueKind == JsonValueKind.Object)
            {
                mainDeck = ParseDeck(md);
            }

            if (dataObj.TryGetProperty("subDecks", out var sdArr) && sdArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in sdArr.EnumerateArray())
                {
                    subDecks.Add(ParseDeck(sub));
                }
            }
        }

        return new JitenDeckDetailResult(mainDeck, subDecks);
    }

    public async Task<JitenDifficultyResult> GetDifficultyAsync(long deckId, CancellationToken ct = default)
    {
        string url = $"{_baseApiUrl}/media-deck/{deckId}/difficulty";
        using var req = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return ParseDifficulty(deckId, doc.RootElement);
    }

    public async Task<JitenDeckStatsResult> GetStatsAsync(long deckId, CancellationToken ct = default)
    {
        string url = $"{_baseApiUrl}/media-deck/{deckId}/stats";
        using var req = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return ParseStats(deckId, doc.RootElement);
    }

    public async Task<IReadOnlyList<JitenVocabularyPreviewWord>> GetVocabularyPreviewAsync(long deckId, int limit = 15, CancellationToken ct = default)
    {
        string url = $"{_baseApiUrl}/media-deck/{deckId}/vocabulary-preview?limit={limit}";
        using var req = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return ParseVocabularyPreview(doc.RootElement);
    }

    public async Task<byte[]> DownloadDeckCsvAsync(long deckId, CancellationToken ct = default)
    {
        string url = $"{_baseApiUrl}/media-deck/{deckId}/download";
        var payload = new
        {
            format = 2, // CSV
            downloadType = 1, // Full
            order = 1 // Chronological
        };
        using var req = CreateRequest(HttpMethod.Post, url);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}
