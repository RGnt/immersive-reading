using System.Net;
using System.Text;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Llm;
using ImmersiveReading.Plugins.AiCompanion.Providers;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>
/// The address of a model server can be typed as host, base or full chat URL. A fake server behaves like llama-server measured on port 8091:
/// 415 on GET /, 404 on /v1 and on POST /, 200 on the model lists and on both chat URLs.
/// </summary>
public class LlmEndpointTests
{
    private sealed class FakeLlamaServer(bool rejectJsonMode = false, HttpStatusCode? chatStatus = null) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public List<string> ChatBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");
            if (request.Method == HttpMethod.Get)
            {
                return path switch
                {
                    "/v1/models" or "/models" => Json("{\"data\":[{\"id\":\"gemma-4\"}]}"),
                    "/" => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                };
            }

            if (path is "/chat/completions" or "/v1/chat/completions")
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                ChatBodies.Add(body);
                if (chatStatus is { } status) return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"nope\"}") };
                if (rejectJsonMode && body.Contains("response_format"))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"response_format is not supported\"}") };
                return Json("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"ok\\\"}\"}}]}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    private sealed class AnswerDto { public string Answer { get; set; } = ""; }

    [Theory]
    [InlineData("http://localhost:8091")]
    [InlineData("http://localhost:8091/")]
    [InlineData("http://localhost:8091/v1")]
    [InlineData("http://localhost:8091/v1/")]
    [InlineData("http://localhost:8091/v1/chat/completions")]
    public void Every_form_of_the_address_gives_the_same_chat_url(string typed)
        => Assert.Equal("http://localhost:8091/v1/chat/completions", LlmEndpoint.ChatCompletionsUrl(typed));

    [Fact]
    public void A_base_with_another_path_is_kept()
    {
        Assert.Equal("https://host/openai/v1/chat/completions", LlmEndpoint.ChatCompletionsUrl("https://host/openai/v1"));
        Assert.Equal(["https://host/openai/v1/models"], LlmEndpoint.ModelsUrls("https://host/openai/v1/chat/completions"));
    }

    [Fact]
    public void The_bare_host_lists_models_with_v1_first_and_ollama_adds_its_own()
    {
        Assert.Equal(["http://localhost:8091/v1/models", "http://localhost:8091/models"], LlmEndpoint.ModelsUrls("http://localhost:8091"));
        Assert.Contains("http://localhost:11434/api/tags", LlmEndpoint.ModelsUrls("http://localhost:11434/v1"));
    }

    [Theory]
    [InlineData("http://localhost:8091")]
    [InlineData("http://localhost:8091/v1")]
    [InlineData("http://localhost:8091/v1/chat/completions")]
    public async Task The_connection_test_succeeds_for_every_form(string typed)
    {
        var server = new FakeLlamaServer();
        var result = await LlmEndpointTester.TestAsync(typed, "", "", 5, handler: server);
        Assert.True(result.Success, result.StatusMessage);
        Assert.Contains("gemma-4", result.Models);
    }

    [Fact]
    public async Task The_connection_test_names_what_it_tried_when_nothing_answers_well()
    {
        var result = await LlmEndpointTester.TestAsync("http://localhost:8091/wrong", "", "", 5, handler: new FakeLlamaServer());
        Assert.False(result.Success);
        Assert.Contains("HTTP 404", result.StatusMessage);
        Assert.Contains("http://localhost:8091/v1", result.StatusMessage);
    }

    [Fact]
    public async Task The_connection_test_stops_when_the_server_is_not_there()
    {
        var result = await LlmEndpointTester.TestAsync("http://localhost:8091", "", "", 5, handler: new Throwing());
        Assert.False(result.Success);
        Assert.Contains("connection refused", result.StatusMessage);
    }

    [Theory]
    [InlineData("http://localhost:8091")]
    [InlineData("http://localhost:8091/v1")]
    [InlineData("http://localhost:8091/v1/chat/completions")]
    public async Task The_provider_reaches_the_chat_endpoint_for_every_form(string typed)
    {
        var server = new FakeLlamaServer();
        var provider = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions(typed, "gemma-4"), new HttpClient(server));
        var response = await provider.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")]));
        Assert.Equal("ok", response.Value.Answer);
        Assert.Equal("POST /v1/chat/completions", server.Calls.Single());
    }

    [Fact]
    public async Task An_http_error_is_reported_with_its_status_and_never_hidden_by_the_offline_fallback()
    {
        var server = new FakeLlamaServer(chatStatus: HttpStatusCode.NotFound);
        var provider = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions("http://localhost:8091", "gemma-4", EnableOfflineHeuristicFallback: true), new HttpClient(server));
        var ex = await Assert.ThrowsAsync<LlmEndpointException>(() => provider.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")])));
        Assert.Contains("HTTP 404", ex.Message);
    }

    [Fact]
    public async Task A_server_that_rejects_json_mode_is_asked_again_without_it()
    {
        var server = new FakeLlamaServer(rejectJsonMode: true);
        var provider = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions("http://localhost:8091/v1", "gemma-4"), new HttpClient(server));
        var response = await provider.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")]));
        Assert.Equal("ok", response.Value.Answer);
        Assert.Equal(2, server.ChatBodies.Count);
        Assert.Contains("response_format", server.ChatBodies[0]);
        Assert.DoesNotContain("response_format", server.ChatBodies[1]);
    }

    [Fact]
    public async Task An_unreachable_server_uses_the_fallback_only_when_it_is_switched_on()
    {
        var on = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions("http://localhost:8091", "m", EnableOfflineHeuristicFallback: true), new HttpClient(new Throwing()));
        await on.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")]));   // does not throw

        var off = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions("http://localhost:8091", "m", EnableOfflineHeuristicFallback: false), new HttpClient(new Throwing()));
        var ex = await Assert.ThrowsAsync<LlmEndpointException>(() => off.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")])));
        Assert.Contains("not reachable", ex.Message);
    }

    [Fact]
    public async Task The_companions_own_endpoint_wins_over_the_app_wide_one_and_clearing_it_goes_back()
    {
        var server = new FakeLlamaServer();
        // the app-wide setting still points at Ollama (not running); Settings -> AI holds the llama-server
        var provider = new OpenAiCompatibleLlmProvider(new LlmEndpointOptions("http://localhost:11434/v1/chat/completions", "llama3.2", EnableOfflineHeuristicFallback: false), new HttpClient(server));
        provider.UseEndpointOverride("http://127.0.0.1:8091/v1", "key", "qwen3-8b");
        var response = await provider.CompleteStructuredAsync<AnswerDto>(new LlmRequest([new LlmMessage("user", "hi")]));
        Assert.Equal("ok", response.Value.Answer);
        Assert.Equal("qwen3-8b", response.ModelId);
        Assert.Equal("http://127.0.0.1:8091/v1", provider.Options.EndpointUrl);

        provider.UseEndpointOverride("", "", "");
        Assert.Equal("http://localhost:11434/v1/chat/completions", provider.Options.EndpointUrl);

        // an app-wide settings change keeps the override
        provider.UseEndpointOverride("http://127.0.0.1:8091/v1", null, null);
        provider.UpdateOptions(new LlmEndpointOptions("http://localhost:1/v1", "other", TimeoutSeconds: 30));
        Assert.Equal("http://127.0.0.1:8091/v1", provider.Options.EndpointUrl);
        Assert.Equal("other", provider.Options.ModelId);
        Assert.Equal(30, provider.Options.TimeoutSeconds);
    }
}
