using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ImmersiveReading.Core.Llm;

public sealed record LlmEndpointTestResult(
    bool Success,
    long LatencyMs,
    string StatusMessage,
    IReadOnlyList<string> Models,
    string ActiveModel);

/// <summary>
/// Probes an OpenAI-compatible / Ollama endpoint's model-list URL to verify connectivity.
/// </summary>
public static class LlmEndpointTester
{
    public static async Task<LlmEndpointTestResult> TestAsync(
        string endpoint,
        string apiKey,
        string modelId,
        int timeoutSeconds,
        CancellationToken ct = default,
        HttpMessageHandler? handler = null)
    {
        var sw = Stopwatch.StartNew();
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 30));
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var models = new List<string>();
        bool endpointReached = false;
        string statusMessage;
        var tried = new List<string>();

        if (LlmEndpoint.IsOffline(endpoint))
        {
            return new LlmEndpointTestResult(false, 0, "No endpoint is configured.", string.IsNullOrWhiteSpace(modelId) ? [] : [modelId], modelId);
        }

        foreach (string url in LlmEndpoint.ModelsUrls(endpoint))
        {
            try
            {
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    tried.Add($"{url} answered HTTP {(int)response.StatusCode}");
                    continue;
                }

                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                ReadModels(doc.RootElement, models);
                endpointReached = true;
                sw.Stop();
                statusMessage = $"Connected to {url} (HTTP {(int)response.StatusCode}) in {sw.ElapsedMilliseconds} ms, {models.Count} model(s)";
                return Finish();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                tried.Add($"{url}: {(ex is TaskCanceledException ? "no answer in time" : ex.Message)}");
                if (ex is HttpRequestException) break;               // the server is not there: the other forms of the address will not help
            }
        }

        sw.Stop();
        statusMessage = "Could not read the model list. " + string.Join("; ", tried) +
            ". The address can be the host (http://localhost:8091), the base (http://localhost:8091/v1) or the full chat URL.";
        return Finish();

        LlmEndpointTestResult Finish()
        {
            if (!string.IsNullOrWhiteSpace(modelId) && !models.Contains(modelId)) models.Insert(0, modelId);
            return new LlmEndpointTestResult(endpointReached, sw.ElapsedMilliseconds, statusMessage, models, modelId);
        }
    }

    private static void ReadModels(JsonElement root, List<string> models)
    {
        if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in dataArr.EnumerateArray())
            {
                if (el.TryGetProperty("id", out var idProp) && idProp.GetString() is { Length: > 0 } m) models.Add(m);
            }
        }
        else if (root.TryGetProperty("models", out var listed) && listed.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in listed.EnumerateArray())
            {
                // Ollama lists "models" with a name (llama.cpp also answers "data", handled above)
                if (el.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { Length: > 0 } m) models.Add(m);
            }
        }
    }
}
