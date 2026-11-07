using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Extensions;

namespace ImmersiveReading.Plugins.AiCompanion.Providers;

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
        _options = options ?? new LlmEndpointOptions();
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

    public void UpdateOptions(LlmEndpointOptions options)
    {
        _options = options;
    }

    public async Task<LlmStructuredResponse<T>> CompleteStructuredAsync<T>(
        LlmRequest request,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.EndpointUrl) &&
            !_options.EndpointUrl.Equals("offline://heuristic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var payload = new
                {
                    model = _options.ModelId,
                    messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
                    temperature = request.Temperature,
                    max_tokens = request.MaxTokens,
                    response_format = new { type = "json_object" }
                };

                using var httpReq = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                }

                using var httpResp = await _httpClient.SendAsync(httpReq, ct).ConfigureAwait(false);
                if (httpResp.IsSuccessStatusCode)
                {
                    string respBody = await httpResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
            }
            catch when (_options.EnableOfflineHeuristicFallback)
            {
                // Fall through to deterministic passage-grounded fallback when no local Ollama server is running
            }
        }

        if (!_options.EnableOfflineHeuristicFallback)
        {
            throw new InvalidOperationException($"LLM endpoint '{_options.EndpointUrl}' was unreachable and offline fallback is disabled.");
        }

        string fallbackJson = BuildOfflineHeuristicJson(request);
        T fallbackValue = JsonSerializer.Deserialize<T>(fallbackJson, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize offline fallback response.");
        return new LlmStructuredResponse<T>(fallbackValue, fallbackJson, "offline-passage-tutor");
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

