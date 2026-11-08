using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ImmersiveReading.Plugins.AiCompanion.Providers;

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
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 30)) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        var models = new List<string>();
        bool endpointReached = false;
        string statusMessage = "";

        try
        {
            string modelsUrl = endpoint.Replace("/chat/completions", "/models");
            if (modelsUrl.Contains("localhost:11434") && !modelsUrl.Contains("/v1/models"))
            {
                modelsUrl = "http://localhost:11434/api/tags";
            }

            var response = await client.GetAsync(modelsUrl, ct).ConfigureAwait(false);
            sw.Stop();
            if (response.IsSuccessStatusCode)
            {
                endpointReached = true;
                statusMessage = $"Connected (HTTP {(int)response.StatusCode}) in {sw.ElapsedMilliseconds} ms";
                var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in dataArr.EnumerateArray())
                    {
                        if (el.TryGetProperty("id", out var idProp))
                        {
                            string m = idProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(m)) models.Add(m);
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("models", out var ollamaArr) && ollamaArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in ollamaArr.EnumerateArray())
                    {
                        if (el.TryGetProperty("name", out var nameProp))
                        {
                            string m = nameProp.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(m)) models.Add(m);
                        }
                    }
                }
            }
            else
            {
                statusMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            statusMessage = $"Connection failed: {ex.Message}";
        }

        if (!string.IsNullOrWhiteSpace(modelId) && !models.Contains(modelId))
        {
            models.Insert(0, modelId);
        }

        return new LlmEndpointTestResult(endpointReached, sw.ElapsedMilliseconds, statusMessage, models, modelId);
    }
}

