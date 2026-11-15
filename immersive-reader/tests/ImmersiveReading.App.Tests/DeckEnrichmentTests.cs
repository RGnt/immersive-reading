using System.Net;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Plugins.DeckBuilder;
using ImmersiveReading.Plugins.DeckBuilder.Enrichment;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>LLM enrichment of decks (spec 16): grounding/validation of model output, the chat client, persistence, deck-grammar.json.</summary>
public class DeckEnrichmentTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ir-enrich-" + Guid.NewGuid().ToString("N"));

    public DeckEnrichmentTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeLlm(params Func<string, JsonDocument>[] answers) : IDeckLlm
    {
        private int _call;
        public List<string> UserPrompts { get; } = [];
        public string ModelId => "fake-model";

        public Task<JsonDocument> CompleteJsonAsync(string system, string user, CancellationToken ct)
        {
            UserPrompts.Add(user);
            var answer = answers[Math.Min(_call++, answers.Length - 1)];
            return Task.FromResult(answer(user));
        }
    }

    private static Func<string, JsonDocument> Json(string json) => _ => JsonDocument.Parse(json);

    private static ChapterSentences Chapter(int index, string title, params string[] sentences) => new(index, title, sentences);

    // ---- grounding ---------------------------------------------------------------------------------

    [Fact]
    public async Task Enricher_KeepsVerifiedExamples_AndDropsInventedOnes()
    {
        var chapter = Chapter(0, "第一章", "猫が魚を食べてしまった。", "彼は静かに笑った。");
        var llm = new FakeLlm(Json("""
            {"grammarPoints":[
              {"pattern":"〜てしまう","category":"grammar","meaning":"do completely / regret","explanation":"Completion with regret.",
               "examples":[{"sentenceId":1,"surface":"食べてしまった"},{"sentenceId":2,"surface":"食べてしまった"}]},
              {"pattern":"〜ながら","category":"grammar","meaning":"while","explanation":"x",
               "examples":[{"sentenceId":1,"surface":"歩きながら"},{"sentenceId":99,"surface":"静かに"}]}
            ],"particleNotes":[]}
            """));

        var result = await new DeckEnricher(llm).RunAsync([chapter], ["が"]);

        var point = Assert.Single(result.Points);
        Assert.Equal("〜てしまう", point.Pattern);
        // Example 2 names sentence 2 but "食べてしまった" is not in it, so only sentence 1 survives.
        var example = Assert.Single(point.Examples);
        Assert.Equal("猫が魚を食べてしまった。", example.Text);
        Assert.Equal("食べてしまった", example.Surface);
        // 〜ながら had no verifiable example (wrong surface / unknown sentence id), so it is dropped and reported.
        Assert.Contains(result.Warnings, w => w.Contains("〜ながら"));
    }

    [Fact]
    public async Task Enricher_ParticleNotes_OnlyForKnownFunctionWords_AndMergeDistinctUses()
    {
        var chapter = Chapter(0, "c", "学校に行く。", "朝に起きる。");
        var llm = new FakeLlm(Json("""
            {"grammarPoints":[],"particleNotes":[
              {"word":"に","uses":[
                {"function":"destination","note":"marks where you go","example":{"sentenceId":1,"surface":"学校に"}},
                {"function":"time","note":"marks a point in time","example":{"sentenceId":2,"surface":"朝に"}},
                {"function":"Destination","note":"duplicate","example":{"sentenceId":1,"surface":"学校に"}}]},
              {"word":"ぞ","uses":[{"function":"emphasis","note":"not in the deck"}]}
            ]}
            """));

        var result = await new DeckEnricher(llm).RunAsync([chapter], ["に", "は"]);

        var uses = Assert.Single(result.FunctionUses).Value;
        Assert.Equal(["destination", "time"], uses.Select(u => u.Function).ToArray());
        Assert.Equal("学校に行く。", uses[0].Example);
        Assert.Equal("destination: marks where you go ; time: marks a point in time", DeckEnricher.ToUsageNote(uses));
    }

    [Fact]
    public async Task Enricher_SamePatternInLaterChapter_AddsExamplesAndKeepsFirstChapter()
    {
        var c1 = Chapter(0, "一", "食べてしまった。");
        var c2 = Chapter(1, "二", "忘れてしまった。");
        var llm = new FakeLlm(
            Json("""{"grammarPoints":[{"pattern":"〜てしまう","category":"grammar","meaning":"m","explanation":"e","examples":[{"sentenceId":1,"surface":"食べてしまった"}]}]}"""),
            Json("""{"grammarPoints":[{"pattern":"～てしまう","examples":[{"sentenceId":1,"surface":"忘れてしまった"}]}]}"""));

        var result = await new DeckEnricher(llm).RunAsync([c1, c2], []);

        var point = Assert.Single(result.Points);
        Assert.Equal(0, point.FirstChapterIndex);
        Assert.Equal(2, point.Examples.Count);
        Assert.Equal("m", point.Meaning); // the later, shorter answer does not overwrite the first explanation
        // the second request tells the model which patterns are already known
        Assert.Contains("〜てしまう", llm.UserPrompts[1]);
    }

    [Fact]
    public async Task Enricher_ProgressCarriesLiveCounters_SoARunningJobShowsRealNumbers()
    {
        var chapters = new[] { Chapter(0, "a", "食べてしまった。"), Chapter(1, "b", "忘れてしまった。"), Chapter(2, "c", "文三。") };
        var llm = new FakeLlm(
            Json("""{"grammarPoints":[{"pattern":"〜てしまう","examples":[{"sentenceId":1,"surface":"食べてしまった"}]}],"particleNotes":[{"word":"を","uses":[{"function":"object","note":"n"}]}]}"""),
            _ => throw new DeckLlmException("HTTP 500"),
            Json("""{"grammarPoints":[],"particleNotes":[]}"""));
        var seen = new List<EnrichmentProgress>();

        await new DeckEnricher(llm).RunAsync(chapters, ["を"], seen.Add);

        Assert.Equal((0, 0, 0, 0), (seen[0].GrammarPoints, seen[0].FunctionWords, seen[0].ChunksFailed, seen[0].WarningCount));      // before anything is done
        Assert.Equal((1, 1, 0, 0), (seen[1].GrammarPoints, seen[1].FunctionWords, seen[1].ChunksFailed, seen[1].WarningCount));      // after chunk 1
        Assert.Equal((1, 1, 1, 1), (seen[2].GrammarPoints, seen[2].FunctionWords, seen[2].ChunksFailed, seen[2].WarningCount));      // after the failed chunk 2
        var last = seen[^1];
        Assert.Equal((3, 3, 1, 1, 2), (last.Done, last.Total, last.ChunksFailed, last.GrammarPoints, last.WarningCount));
        Assert.Contains("HTTP 500", last.RecentWarnings![0]);
    }

    [Fact]
    public async Task Enricher_AnAnswerWhoseExamplesCannotBeVerified_IsAWarningNotSilentSuccess()
    {
        var chapters = new[] { Chapter(0, "a", "文一。") };
        var llm = new FakeLlm(Json("""{"grammarPoints":[{"pattern":"〜ばかり","examples":[{"sentenceId":1,"surface":"こんな文はない"}]}],"particleNotes":[]}"""));

        var result = await new DeckEnricher(llm).RunAsync(chapters, []);

        Assert.Empty(result.Points);
        Assert.Contains(result.Warnings, w => w.Contains("no example could be verified"));
        Assert.Contains(result.Warnings, w => w.Contains("nothing in the answer could be used") && w.Contains("grammarPoints: 1, particleNotes: 0"));
    }

    [Fact]
    public async Task Enricher_ANewExampleForAKnownPattern_CountsAsContribution_NoWarning()
    {
        var chapters = new[] { Chapter(0, "a", "食べてしまった。"), Chapter(1, "b", "忘れてしまった。") };
        var llm = new FakeLlm(
            Json("""{"grammarPoints":[{"pattern":"〜てしまう","examples":[{"sentenceId":1,"surface":"食べてしまった"}]}]}"""),
            Json("""{"grammarPoints":[{"pattern":"〜てしまう","examples":[{"sentenceId":1,"surface":"忘れてしまった"}]}]}"""));

        var result = await new DeckEnricher(llm).RunAsync(chapters, []);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, Assert.Single(result.Points).Examples.Count);
    }

    [Fact]
    public async Task Enricher_FailedChunkIsReported_ButOtherChunksStillRun_AndFatalErrorsStop()
    {
        var chapters = new[] { Chapter(0, "a", "文一。"), Chapter(1, "b", "文二。") };
        int calls = 0;
        var flaky = new FakeLlm(
            _ => { calls++; throw new DeckLlmException("The LLM answer was not valid JSON."); },
            Json("""{"grammarPoints":[],"particleNotes":[]}"""));

        var result = await new DeckEnricher(flaky).RunAsync(chapters, []);
        Assert.Equal(1, result.ChunksFailed);
        Assert.Equal(2, result.ChunksTotal);
        Assert.Equal(2, result.Warnings.Count);                       // the failed request, and the second answer that contained nothing
        Assert.Contains("not valid JSON", result.Warnings[0]);
        Assert.Contains("nothing in the answer could be used", result.Warnings[1]);

        var fatal = new FakeLlm(_ => throw new DeckLlmException("The LLM endpoint answered HTTP 401: bad key") { Data = { ["fatal"] = true } });
        await Assert.ThrowsAsync<DeckLlmException>(() => new DeckEnricher(fatal).RunAsync(chapters, []));
    }

    [Fact]
    public void Enricher_SplitsLongChaptersIntoChunks_AndNumbersSentencesPerChunk()
    {
        var sentences = Enumerable.Range(0, 10).Select(i => new string('あ', 100) + i).ToArray();
        var enricher = new DeckEnricher(new FakeLlm(), new DeckEnrichmentOptions(MaxChunkChars: 350));

        var chunks = enricher.Chunk([Chapter(3, "長い章", sentences)]);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.Equal(3, c.ChapterIndex));
        Assert.Equal(10, chunks.Sum(c => c.Sentences.Count));
        Assert.Contains("(part 1/", chunks[0].Title);
    }

    // ---- chat client -------------------------------------------------------------------------------

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        private int _i;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return responses[Math.Min(_i++, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Ok(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "")
        => new(code) { Content = new StringContent(body) };

    private static OpenAiChatClient Client(SequenceHandler handler, string url = "https://api.freetoken.example/v1/chat/completions")
        => new(new HttpClient(handler), () => new DeckLlmConnection(url, "m1", "key"), TimeSpan.FromSeconds(5), delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task Client_SendsAuthAndModel_AndParsesFencedJson()
    {
        var handler = new SequenceHandler(_ => Ok("Here you go:\n```json\n{\"a\":1}\n```"));
        using var doc = await Client(handler).CompleteJsonAsync("sys", "usr", default);

        Assert.Equal(1, doc.RootElement.GetProperty("a").GetInt32());
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("m1", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    // ---- plugin-level endpoint (own endpoint or Settings → AI) --------------------------------------

    private static readonly ImmersiveReading.Core.Config.LlmSettings AppLlm = new(EndpointUrl: "https://app.example/v1/chat/completions", ModelId: "app-model", ApiKey: "app-key");

    [Fact]
    public void Connection_UsesTheOwnEndpointWhenSet_AndSettingsAiOtherwise()
    {
        var own = DeckBuilderPlugin.ResolveConnection(new DeckBuilderSettings { LlmEndpointUrl = "http://127.0.0.1:1919/v1", LlmApiKey = " secret " }, AppLlm);
        Assert.Equal("http://127.0.0.1:1919/v1/chat/completions", own.EndpointUrl);
        Assert.Equal("", own.ModelId);
        Assert.Equal("secret", own.ApiKey);
        Assert.True(own.ModelOptional);
        Assert.True(own.DisableThinking);                 // default "off"

        var fallback = DeckBuilderPlugin.ResolveConnection(new DeckBuilderSettings(), AppLlm);
        Assert.Equal(("https://app.example/v1/chat/completions", "app-model", "app-key"), (fallback.EndpointUrl, fallback.ModelId, fallback.ApiKey));
        Assert.False(fallback.ModelOptional);
        Assert.False(fallback.DisableThinking);           // never changes what Settings → AI sends

        // the own endpoint does not borrow the model or key of Settings → AI
        var isolated = DeckBuilderPlugin.ResolveConnection(new DeckBuilderSettings { LlmEndpointUrl = "http://x/v1", LlmThinking = "default" }, AppLlm);
        Assert.Equal(("", null, false), (isolated.ModelId, isolated.ApiKey, isolated.DisableThinking));
    }

    [Theory]
    [InlineData("http://127.0.0.1:1919/v1", "http://127.0.0.1:1919/v1/chat/completions")]
    [InlineData("http://127.0.0.1:1919/v1/", "http://127.0.0.1:1919/v1/chat/completions")]
    [InlineData("https://host/v1/chat/completions", "https://host/v1/chat/completions")]
    [InlineData("https://host/v1/chat/completions/", "https://host/v1/chat/completions")]
    [InlineData("https://host/custom/api", "https://host/custom/api")]            // an exact URL is never rewritten
    [InlineData("http://localhost:8091", "http://localhost:8091/v1/chat/completions")]
    [InlineData("http://localhost:8091/", "http://localhost:8091/v1/chat/completions")]
    [InlineData("  ", "")]
    [InlineData("offline://none", "offline://none")]
    public void ChatCompletionsUrl_AcceptsBaseOrFullUrls(string input, string expected)
        => Assert.Equal(expected, DeckBuilderPlugin.ChatCompletionsUrl(input));

    [Fact]
    public void Configured_OwnEndpointNeedsNoModel_ButSettingsAiDoes()
    {
        var plugin = typeof(DeckBuilderPlugin).GetMethod("IsConfigured", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        bool Configured(DeckLlmConnection c) => (bool)plugin.Invoke(null, [c])!;

        Assert.True(Configured(new DeckLlmConnection("http://h/v1/chat/completions", "", null, ModelOptional: true)));
        Assert.False(Configured(new DeckLlmConnection("http://h/v1/chat/completions", "", null)));
        Assert.False(Configured(new DeckLlmConnection("offline://x", "m", null, ModelOptional: true)));
        Assert.False(Configured(new DeckLlmConnection("", "m", null)));
    }

    [Fact]
    public async Task Client_OmitsAnEmptyModel_AndSendsEnableThinkingFalseOnlyWhenAsked()
    {
        var handler = new SequenceHandler(_ => Ok("{\"a\":1}"), _ => Ok("{\"a\":1}"));
        var client = new OpenAiChatClient(new HttpClient(handler), () => new DeckLlmConnection("http://h/v1/chat/completions", "", null, ModelOptional: true, DisableThinking: true), TimeSpan.FromSeconds(5));
        using (await client.CompleteJsonAsync("s", "u", default)) { }
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.False(sent.RootElement.TryGetProperty("model", out _));
        Assert.False(sent.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());

        var plain = new OpenAiChatClient(new HttpClient(handler), () => new DeckLlmConnection("http://h/v1/chat/completions", "m", "k"), TimeSpan.FromSeconds(5));
        using (await plain.CompleteJsonAsync("s", "u", default)) { }
        using var sent2 = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("m", sent2.RootElement.GetProperty("model").GetString());
        Assert.False(sent2.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task TestEndpoint_ListsModelsFromTheModelsUrl_AndReportsFailures()
    {
        var handler = new SequenceHandler(
            r =>
            {
                Assert.Equal("http://127.0.0.1:1919/v1/models", r.RequestUri!.ToString());     // base derived from the chat URL
                Assert.Equal("Bearer k", r.Headers.Authorization!.ToString());
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"qwen\",\"context_length\":262144},{\"id\":\"other\"}]}") };
            },
            _ => Status(HttpStatusCode.Unauthorized, "bad key"));
        var conn = new DeckLlmConnection("http://127.0.0.1:1919/v1/chat/completions", "", "k", ModelOptional: true);

        var ok = JsonSerializer.SerializeToElement(await DeckBuilderPlugin.ProbeEndpointAsync(new HttpClient(handler), conn, default));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal(["qwen", "other"], ok.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(262144, ok.GetProperty("advertisedContext").GetInt32());
        Assert.Contains("context tokens", ok.GetProperty("note").GetString());

        var bad = JsonSerializer.SerializeToElement(await DeckBuilderPlugin.ProbeEndpointAsync(new HttpClient(handler), conn, default));
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("401", bad.GetProperty("error").GetString());

        var none = JsonSerializer.SerializeToElement(await DeckBuilderPlugin.ProbeEndpointAsync(new HttpClient(handler), new DeckLlmConnection("", "", null), default));
        Assert.False(none.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Client_WithoutAnyEndpoint_SaysWhereToSetOne()
    {
        var client = new OpenAiChatClient(new HttpClient(new SequenceHandler(_ => Ok("{}"))), () => new DeckLlmConnection("", "", null), TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<DeckLlmException>(() => client.CompleteJsonAsync("s", "u", default));
        Assert.Contains("Deck Builder plugin settings", ex.Message);
        Assert.Contains("Settings → AI", ex.Message);
    }

    [Fact]
    public void Limits_AreTheUsersNumbersUntilAContextSizeIsKnown_ThenRequestsFit()
    {
        var unknown = DeckBuilderPlugin.EffectiveLimits(new DeckBuilderSettings { LlmMaxChunkChars = 60_000, LlmMaxOutputTokens = 8000, LlmMaxPointsPerChunk = 25 });
        Assert.Equal((60_000, 8000, 25), (unknown.ChunkChars, unknown.MaxOutputTokens, unknown.MaxPoints));

        // a server that advertises 262,144 tokens but enforces 8,241 per request (spec 18 section 7)
        var small = DeckBuilderPlugin.EffectiveLimits(new DeckBuilderSettings { LlmContextTokens = 8_241 });
        double promptTokens = small.ChunkChars * 1.5 + 1_500;
        Assert.True(promptTokens + small.MaxOutputTokens <= 8_241, $"{promptTokens} + {small.MaxOutputTokens}");
        Assert.True(small.ChunkChars >= 600 && small.ChunkChars < 60_000);
        Assert.True(small.MaxPoints is >= 3 and < 25);

        // a big window never raises the user's own caps
        var big = DeckBuilderPlugin.EffectiveLimits(new DeckBuilderSettings { LlmContextTokens = 262_144, LlmMaxChunkChars = 20_000 });
        Assert.Equal(20_000, big.ChunkChars);
        Assert.Equal(8000, big.MaxOutputTokens);
    }

    [Fact]
    public async Task Client_RetriesRateLimitsAndServerErrors()
    {
        var handler = new SequenceHandler(_ => Status(HttpStatusCode.TooManyRequests), _ => Status(HttpStatusCode.BadGateway), _ => Ok("{\"ok\":true}"));
        using var doc = await Client(handler).CompleteJsonAsync("s", "u", default);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task Client_AuthFailureIsFatalAndNotRetried_AndShowsTheServerMessage()
    {
        var handler = new SequenceHandler(_ => Status(HttpStatusCode.Unauthorized, "invalid api key"));
        var ex = await Assert.ThrowsAsync<DeckLlmException>(() => Client(handler).CompleteJsonAsync("s", "u", default));
        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid api key", ex.Message);
        Assert.True(ex.Data.Contains("fatal"));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Client_RetriesWithoutJsonModeWhenTheServerRejectsResponseFormat()
    {
        var handler = new SequenceHandler(
            _ => Status(HttpStatusCode.BadRequest, "{\"error\":\"response_format is not supported\"}"),
            _ => Ok("{\"x\":2}"));
        using var doc = await Client(handler).CompleteJsonAsync("s", "u", default);

        Assert.Equal(2, doc.RootElement.GetProperty("x").GetInt32());
        Assert.Contains("response_format", handler.Bodies[0]);
        Assert.DoesNotContain("response_format", handler.Bodies[1]);
    }

    [Fact]
    public async Task Client_InvalidJsonIsRetried_ThenReportedAsNonFatal()
    {
        var handler = new SequenceHandler(_ => Ok("not json at all"));
        var ex = await Assert.ThrowsAsync<DeckLlmException>(() => Client(handler).CompleteJsonAsync("s", "u", default));
        Assert.False(ex.Data.Contains("fatal"));
        Assert.True(handler.Bodies.Count > 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("offline://heuristic")]
    public async Task Client_RefusesWithoutARealEndpoint_NeverFakesAnAnswer(string url)
    {
        var handler = new SequenceHandler(_ => Ok("{}"));
        var ex = await Assert.ThrowsAsync<DeckLlmException>(() => Client(handler, url).CompleteJsonAsync("s", "u", default));
        Assert.True(ex.Data.Contains("fatal"));
        Assert.Empty(handler.Bodies);
    }

    // ---- persistence and deck-grammar.json ------------------------------------------------------------

    private static EnrichmentResult SampleResult()
    {
        var point = new GrammarPoint { Id = "dg-abc", Pattern = "〜てしまう", Category = "grammar", Meaning = "m", Explanation = "e", FirstChapterIndex = 2, FirstChapterTitle = "三" };
        point.Examples.Add(new GrammarExample("食べてしまった。", "食べてしまった", 2));
        return new EnrichmentResult
        {
            Model = "fake-model",
            BookStamp = "123",
            ChunksTotal = 1,
            Points = [point],
            FunctionUses = { ["に"] = [new FunctionUse("destination", "where to", "学校に行く。")] }
        };
    }

    [Fact]
    public void Store_RoundTripsIncludingExamples_AndIgnoresAChangedBook()
    {
        var store = new EnrichmentStore(Path.Combine(_tempDir, "store"));
        store.Save("book-1", SampleResult());

        var loaded = store.Load("book-1", "123");
        Assert.NotNull(loaded);
        Assert.Equal("食べてしまった", Assert.Single(Assert.Single(loaded!.Points).Examples).Surface);
        Assert.Equal("where to", loaded.FunctionUses["に"][0].Note);

        Assert.Null(store.Load("book-1", "999")); // the EPUB changed: the old enrichment no longer applies
        Assert.Null(store.Load("other-book", "123"));
    }

    [Fact]
    public void DeckGrammarJson_HasTheContractThePythonPipelineReads()
    {
        using var doc = JsonDocument.Parse(DeckGrammarJson.Render(SampleResult(), "本のタイトル"));
        var root = doc.RootElement;

        Assert.Equal("immersive-deck-grammar/1", root.GetProperty("schema").GetString());
        Assert.Equal("llm_generated_examples_text_verified", root.GetProperty("status").GetString());
        Assert.Equal("fake-model", root.GetProperty("generator").GetProperty("model").GetString());
        var g = root.GetProperty("grammar")[0];
        Assert.Equal("〜てしまう", g.GetProperty("pattern").GetString());
        Assert.Equal("食べてしまった。", g.GetProperty("examples")[0].GetProperty("text").GetString());
        Assert.Equal("destination", root.GetProperty("function_word_notes").GetProperty("に")[0].GetProperty("function").GetString());
    }
}
