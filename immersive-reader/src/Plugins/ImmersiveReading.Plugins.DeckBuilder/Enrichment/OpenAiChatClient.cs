using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ImmersiveReading.Plugins.DeckBuilder.Enrichment;

/// <summary>A chat model that answers with one JSON document.</summary>
public interface IDeckLlm
{
    string ModelId { get; }

    /// <summary>Returns the model's answer as a parsed JSON document. Throws <see cref="DeckLlmException"/> on failure.</summary>
    Task<JsonDocument> CompleteJsonAsync(string system, string user, CancellationToken ct);
}

public sealed class DeckLlmException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="ModelOptional">A plugin-level endpoint may leave the model empty (local servers answer with their loaded model).</param>
/// <param name="DisableThinking">Sends <c>chat_template_kwargs.enable_thinking = false</c>: reasoning models (Qwen3 ...) otherwise spend the whole token budget thinking.</param>
public sealed record DeckLlmConnection(string EndpointUrl, string ModelId, string? ApiKey, bool ModelOptional = false, bool DisableThinking = false);

/// <summary>
/// Minimal OpenAI-compatible chat client (works with FreeToken, Ollama, OpenAI and similar). Unlike the AI companion's
/// provider it never falls back to a canned answer: a deck must come from the model or not at all, and every failure
/// is reported with the HTTP status and body so a wrong endpoint or key is obvious.
/// </summary>
public sealed class OpenAiChatClient : IDeckLlm
{
    private readonly HttpClient _http;
    private readonly Func<DeckLlmConnection> _connection;
    private readonly TimeSpan _requestTimeout;
    private readonly int _maxOutputTokens;
    private readonly double _temperature;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(20)];

    public OpenAiChatClient(
        HttpClient http,
        Func<DeckLlmConnection> connection,
        TimeSpan requestTimeout,
        int maxOutputTokens = 8000,
        double temperature = 0.2,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _connection = connection;
        _requestTimeout = requestTimeout;
        _maxOutputTokens = maxOutputTokens;
        _temperature = temperature;
        _delay = delay ?? Task.Delay;
    }

    public string ModelId => _connection().ModelId;

    public async Task<JsonDocument> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        var conn = _connection();
        if (string.IsNullOrWhiteSpace(conn.EndpointUrl) || conn.EndpointUrl.StartsWith("offline://", StringComparison.OrdinalIgnoreCase))
            throw Fatal(new DeckLlmException("No LLM endpoint is configured. Set one in the Deck Builder plugin settings (LLM endpoint) or in Settings → AI."));
        if (string.IsNullOrWhiteSpace(conn.ModelId) && !conn.ModelOptional)
            throw Fatal(new DeckLlmException("No LLM model id is configured in Settings → AI (or set an endpoint in the Deck Builder plugin settings)."));

        bool useJsonMode = true;
        Exception? last = null;

        for (int attempt = 0; attempt <= Backoff.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string content = await SendAsync(conn, system, user, useJsonMode, ct).ConfigureAwait(false);
                return ParseJson(content);
            }
            catch (JsonModeRejectedException)
            {
                // Some compatible servers reject response_format; retry once without it (the prompt demands JSON anyway).
                useJsonMode = false;
                attempt--;
            }
            catch (DeckLlmException ex) when (ex.Data.Contains("retryable") && attempt < Backoff.Length)
            {
                last = ex;
                await _delay(Backoff[attempt], ct).ConfigureAwait(false);
            }
        }

        throw last ?? new DeckLlmException("The LLM request failed.");
    }

    private async Task<string> SendAsync(DeckLlmConnection conn, string system, string user, bool jsonMode, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["messages"] = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            ["temperature"] = _temperature,
            ["max_tokens"] = _maxOutputTokens
        };
        if (!string.IsNullOrWhiteSpace(conn.ModelId)) body["model"] = conn.ModelId;
        if (conn.DisableThinking) body["chat_template_kwargs"] = new { enable_thinking = false };
        if (jsonMode) body["response_format"] = new { type = "json_object" };

        using var request = new HttpRequestMessage(HttpMethod.Post, conn.EndpointUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(conn.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Retryable(new DeckLlmException($"The LLM did not answer within {_requestTimeout.TotalSeconds:0} s."));
        }
        catch (HttpRequestException ex)
        {
            throw Retryable(new DeckLlmException($"Could not reach the LLM endpoint: {ex.Message}", ex));
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string snippet = text.Length > 400 ? text[..400] : text;
                if (jsonMode && response.StatusCode == HttpStatusCode.BadRequest && text.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                    throw new JsonModeRejectedException();

                var ex = new DeckLlmException($"The LLM endpoint answered HTTP {(int)response.StatusCode}: {snippet}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    throw Retryable(ex);
                throw Fatal(ex);
            }

            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw Fatal(new DeckLlmException("The LLM endpoint answered with an unexpected response shape (not an OpenAI chat completion).", ex));
            }
        }
    }

    /// <summary>Accepts bare JSON or JSON wrapped in a Markdown fence / surrounding prose.</summary>
    internal static JsonDocument ParseJson(string content)
    {
        string trimmed = content.Trim();
        int start = trimmed.IndexOf('{');
        int end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw Retryable(new DeckLlmException("The LLM answer contained no JSON object."));
        try
        {
            return JsonDocument.Parse(trimmed[start..(end + 1)]);
        }
        catch (JsonException ex)
        {
            throw Retryable(new DeckLlmException("The LLM answer was not valid JSON.", ex));
        }
    }

    /// <summary>Marks an error that retrying or moving on to the next chunk cannot fix (bad key, wrong URL, no model).</summary>
    private static DeckLlmException Fatal(DeckLlmException ex)
    {
        ex.Data["fatal"] = true;
        return ex;
    }

    private static DeckLlmException Retryable(DeckLlmException ex)
    {
        ex.Data["retryable"] = true;
        return ex;
    }

    private sealed class JsonModeRejectedException : Exception;
}
