using ImmersiveReading.Core.Llm;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.Plugins.AiCompanion.Providers;

/// <summary>The model server answered with an error, or cannot be reached with the fallback off. The message says what to fix.</summary>
public sealed class LlmEndpointException(string message) : InvalidOperationException(message);

/// <summary>The three model fields of the companion's own settings panel (Settings → AI). Empty means "use the app-wide LLM settings".</summary>
public sealed class CompanionEndpointSettings
{
    public string EndpointUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ModelId { get; set; } = "";
}

public sealed record LlmEndpointOptions(
    string EndpointUrl = "http://localhost:11434/v1/chat/completions",
    string ModelId = "llama3.2",
    string? ApiKey = null,
    int TimeoutSeconds = 12,
    bool EnableOfflineHeuristicFallback = true
);

public sealed class OpenAiCompatibleLlmProvider : ILlmProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private LlmEndpointOptions _options;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string ProviderId => "openai-compatible";
    public string DisplayName => $"OpenAI/Ollama Compatible ({_options.ModelId})";
    public bool IsLocalOffline => _options.EndpointUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                               || _options.EndpointUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

    public LlmEndpointOptions Options => _options;

    public OpenAiCompatibleLlmProvider(LlmEndpointOptions? options = null, HttpClient? httpClient = null)
    {
        _options = _baseOptions = options ?? new LlmEndpointOptions();
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
        else
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(2, _options.TimeoutSeconds))
            };
            _disposeClient = true;
        }
    }

    /// <summary>The model id the answer carries when no model was asked (the offline stand-in); callers that need a real model check it.</summary>
    public const string OfflineModelId = "offline-passage-tutor";

    private LlmEndpointOptions _baseOptions = null!;
    private (string Url, string? Key, string? Model)? _override;

    /// <summary>The app-wide LLM settings (timeout, fallback, and the endpoint when the companion has none of its own).</summary>
    public void UpdateOptions(LlmEndpointOptions options)
    {
        _baseOptions = options;
        Recompute();
    }

    /// <summary>The endpoint, key and model typed in the companion's own settings (Settings → AI); they win over the app-wide ones. Null clears it.</summary>
    public void UseEndpointOverride(string? url, string? apiKey, string? modelId)
    {
        _override = string.IsNullOrWhiteSpace(url) ? null : (url.Trim(), string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim());
        Recompute();
    }

    private void Recompute()
    {
        _options = _override is { } o ? _baseOptions with { EndpointUrl = o.Url, ApiKey = o.Key, ModelId = o.Model ?? _baseOptions.ModelId } : _baseOptions;
    }

    public async Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
        LlmRequest request,
        CancellationToken ct = default)
    {
        if (!LlmEndpoint.IsOffline(_options.EndpointUrl) &&
            !_options.EndpointUrl.Equals("offline://heuristic", StringComparison.OrdinalIgnoreCase))
        {
            string url = LlmEndpoint.ChatCompletionsUrl(_options.EndpointUrl);
            try
            {
                bool jsonMode = true;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["model"] = _options.ModelId,
                        ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                        ["temperature"] = request.Temperature,
                        ["max_tokens"] = request.MaxTokens
                    };
                    if (jsonMode) payload["response_format"] = new { type = "json_object" };

                    using var httpReq = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                    {
                        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                    }

                    using var httpResp = await _httpClient.SendAsync(httpReq, ct).ConfigureAwait(false);
                    string respBody = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (httpResp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(respBody);
                        string contentJson = doc.RootElement
                            .GetProperty("choices")[0]
                            .GetProperty("message")
                            .GetProperty("content")
                            .GetString() ?? "{}";

                        contentJson = StripMarkdownJsonFence(contentJson);
                        T parsed = JsonSerializer.Deserialize<T>(contentJson, JsonOpts)
                            ?? throw new InvalidOperationException("Failed to deserialize LLM JSON response.");

                        return new LlmStructuredResponse<T>(parsed, contentJson, _options.ModelId);
                    }

                    // some servers reject response_format: ask again without it (the prompt demands JSON anyway)
                    if (jsonMode && (int)httpResp.StatusCode == 400 && respBody.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonMode = false;
                        continue;
                    }

                    // The server answered and said no: a wrong address, key or model, not a missing server. It is reported with the real
                    // status and never hidden behind the offline fallback.
                    string snippet = respBody.Length > 300 ? respBody[..300] : respBody;
                    throw new LlmEndpointException($"The LLM server answered HTTP {(int)httpResp.StatusCode} at {url}: {snippet}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // the server is not reachable at all: the passage-grounded fallback keeps the feature usable offline
                if (!_options.EnableOfflineHeuristicFallback)
                {
                    throw new LlmEndpointException($"The LLM server at {url} is not reachable (and the offline fallback is switched off): {ex.Message}");
                }
            }
        }

        if (!_options.EnableOfflineHeuristicFallback)
        {
            throw new LlmEndpointException($"The LLM server at {_options.EndpointUrl} is not reachable (and the offline fallback is switched off).");
        }

        string fallbackJson = BuildOfflineHeuristicJson(request);
        T fallbackValue = JsonSerializer.Deserialize<T>(fallbackJson, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize offline fallback response.");
        return new LlmStructuredResponse<T>(fallbackValue, fallbackJson, OfflineModelId);
    }

    private static string BuildOfflineHeuristicJson(LlmRequest request)
    {
        string userPrompt = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        string kind = request.TaskKind ?? "";

        if (kind == "quiz.generate.mcq")
        {
            string sentence = ExtractFirstSentenceFromPassage(userPrompt);
            var payload = new
            {
                questionPrompt = "いま読んだ場面の内容として、本文の記述と合っているものはどれですか？",
                options = new[]
                {
                    sentence,
                    "まだ登場していない別の街へ引っ越す場面が描かれている。",
                    "登場人物たちが宇宙船に乗って旅立つ場面が描かれている。",
                    "物語の最初から10年後の出来事が語られている。"
                },
                correctOptionIndex = 0,
                evidenceQuote = sentence,
                paragraphId = 0,
                explanation = "本文の該当段落にそのまま記述されている内容が正解です。"
            };
            return JsonSerializer.Serialize(payload, JsonOpts);
        }

        if (kind == "quiz.generate.guided" || kind == "quiz.generate.freeform")
        {
            string sentence = ExtractFirstSentenceFromPassage(userPrompt);
            var payload = new
            {
                questionPrompt = "いま読んだ場面で、どのような出来事や様子が描かれていましたか？本文の言葉を使って説明してください。",
                options = Array.Empty<string>(),
                correctOptionIndex = 0,
                evidenceQuote = sentence,
                paragraphId = 0,
                modelAnswer = $"本文では「{sentence}」と描かれています。"
            };
            return JsonSerializer.Serialize(payload, JsonOpts);
        }

        if (kind == "quiz.grade.freeform")
        {
            string evidence = ExtractFirstSentenceFromPassage(userPrompt);
            var payload = new
            {
                comprehensionScore = 88,
                evidenceQuote = evidence,
                missingOrMisunderstood = "主要な出来事は正確に捉えられています。",
                modelAnswer = $"本文では「{evidence}」と描かれており、登場人物の行動と状況が読み取れます。"
            };
            return JsonSerializer.Serialize(payload, JsonOpts);
        }

        // Default: Persona chat reply
        string targetWord = ExtractTargetWordFromPrompt(userPrompt);
        string replyText = !string.IsNullOrEmpty(targetWord)
            ? $"うん、いっしょに{targetWord}を見に行こうね！とても楽しみだよ。"
            : "うん、そうだね！これからもいっしょに物語を読み進めようね。";

        return JsonSerializer.Serialize(new { reply = replyText }, JsonOpts);
    }

    private static string ExtractFirstSentenceFromPassage(string prompt)
    {
        int marker = prompt.IndexOf("PASSAGE:", StringComparison.OrdinalIgnoreCase);
        string slice = marker >= 0 ? prompt[(marker + "PASSAGE:".Length)..].Trim() : prompt.Trim();
        var lines = slice
            .Split(['\r', '\n', '。'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length >= 4 && !l.StartsWith('[') && !l.StartsWith("TASK:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0) return "物語の場面が進んでいます。";
        string first = lines[0];
        return first.EndsWith('。') ? first : first + "。";
    }

    private static string ExtractTargetWordFromPrompt(string prompt)
    {
        int idx = prompt.IndexOf("TARGET_PLUS_ONE:", StringComparison.Ordinal);
        if (idx < 0) return "";
        string after = prompt[(idx + "TARGET_PLUS_ONE:".Length)..].Trim();
        int endLine = after.IndexOfAny(['\r', '\n']);
        string line = endLine >= 0 ? after[..endLine] : after;
        var parts = line.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.FirstOrDefault() ?? "";
    }

    private static string StripMarkdownJsonFence(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            s = s["```json".Length..].Trim();
        }
        else if (s.StartsWith("```", StringComparison.Ordinal))
        {
            s = s[3..].Trim();
        }
        if (s.EndsWith("```", StringComparison.Ordinal))
        {
            s = s[..^3].Trim();
        }
        return s;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}

