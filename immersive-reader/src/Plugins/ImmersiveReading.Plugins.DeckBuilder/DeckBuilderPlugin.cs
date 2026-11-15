using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Plugins.DeckBuilder.Enrichment;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Plugins.DeckBuilder;

public sealed class DeckBuilderSettings
{
    public string Order { get; set; } = "chronological";
    public bool ExcludeKana { get; set; }
    public bool ExcludeExampleSentences { get; set; }
    public bool ContentWordsOnly { get; set; }
    public bool ExcludeFunctionWords { get; set; }

    // LLM enrichment. Leave the endpoint empty to use Settings → AI (endpoint, model and key).
    /// <summary>OpenAI-compatible endpoint of the deck LLM: either the base URL (…/v1) or the full …/chat/completions URL.</summary>
    public string LlmEndpointUrl { get; set; } = "";
    /// <summary>Empty = the server's loaded model (only with an own endpoint).</summary>
    public string LlmModelId { get; set; } = "";
    public string LlmApiKey { get; set; } = "";
    /// <summary>"off" sends enable_thinking=false (own endpoint only); "default" sends nothing.</summary>
    public string LlmThinking { get; set; } = "off";
    /// <summary>What the server really accepts per request (prompt + answer). 0 = unknown; chunks are then only bounded by LlmMaxChunkChars.</summary>
    public int LlmContextTokens { get; set; }
    public int LlmMaxChunkChars { get; set; } = 60_000;
    public int LlmRequestTimeoutSeconds { get; set; } = 300;
    public int LlmMaxOutputTokens { get; set; } = 8000;
    public int LlmMaxPointsPerChunk { get; set; } = 25;
    public double LlmTemperature { get; set; } = 0.2;
}

/// <summary>
/// Builds jiten.moe-style vocabulary decks locally from a library book (see NOTICE-Jiten.md for attribution).
/// The deck is written next to the book as <c>deck.csv</c>, the file the enrichment pipeline already reads.
/// Optionally an LLM adds the grammar used per chapter and usage notes for particles (deck-grammar.json).
/// </summary>
public sealed class DeckBuilderPlugin(
    ILanguagePluginResolver languages,
    DictionaryDatabase dictionaries,
    IConfigurationManager appConfig,
    HttpClient? llmHttp = null)
    : IImmersivePlugin, IDisposable
{
    public const string PluginIdValue = "deckBuilder";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdValue,
        Name = "Deck Builder (jiten.moe style)",
        Version = "1.1.0",
        Author = "RGnt",
        Description = "Build a vocabulary deck (deck.csv) from a book on your computer, using the method of jiten.moe, and optionally add chapter grammar and particle notes with an LLM. Based on the open-source Jiten project (Apache-2.0).",
        ReloadBehavior = PluginReloadBehavior.Hot,
        UiEntry = "ui.js",
        StyleEntry = "ui.css"
    };

    private sealed record CachedBuild(string Key, DeckBuildResult Result);

    private sealed class EnrichJob
    {
        public string State = "running"; // running | done | failed | cancelled
        public int Done, Total;
        public string Chapter = "";
        // live counters of a running job (the result is only complete, and saved, at the end)
        public int Points, FunctionWords, ChunksFailed, WarningCount;
        public IReadOnlyList<string> RecentWarnings = [];
        public string? Error;
        public CancellationTokenSource Cts = new();
        public EnrichmentResult? Result;
    }

    private IBookCatalog _catalog = null!;
    private DeckBuilderSettings _settings = new();
    private IDisposable? _configSub;
    private CachedBuild? _cache;
    private EnrichmentStore _store = null!;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private readonly ConcurrentDictionary<string, EnrichJob> _jobs = new();
    private readonly HttpClient _http = llmHttp ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        _catalog = context.BookCatalog;
        _settings = context.Config.Get<DeckBuilderSettings>();
        _configSub = context.Config.OnConfigChanged<DeckBuilderSettings>(s => _settings = s);
        _store = new EnrichmentStore(Path.Combine(context.PluginDataDirectory, "enrichment"));

        context.RegisterRpcAction("analyzeBook", AnalyzeBookAsync);
        context.RegisterRpcAction("previewDeck", PreviewDeckAsync);
        context.RegisterRpcAction("exportDeck", ExportDeckAsync);
        context.RegisterRpcAction("testEndpoint", TestEndpointAsync);
        context.RegisterRpcAction("estimateEnrichment", EstimateEnrichment);
        context.RegisterRpcAction("startEnrichment", StartEnrichmentAsync);
        context.RegisterRpcAction("getEnrichmentStatus", GetEnrichmentStatus);
        context.RegisterRpcAction("cancelEnrichment", CancelEnrichment);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        _configSub?.Dispose();
        _configSub = null;
        _cache = null;
        foreach (var job in _jobs.Values) job.Cts.Cancel();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => OnDisableAsync();

    public void Dispose()
    {
        _configSub?.Dispose();
        _buildLock.Dispose();
        if (llmHttp is null) _http.Dispose();
    }

    // ---------------------------------------------------------------- deck RPC

    private async Task<object?> AnalyzeBookAsync(JsonElement p, CancellationToken ct)
    {
        var build = await GetOrBuildAsync(p, force: Bool(p, "refresh") ?? false, ct).ConfigureAwait(false);
        return new { stats = build.Result.Stats };
    }

    private async Task<object?> PreviewDeckAsync(JsonElement p, CancellationToken ct)
    {
        var build = await GetOrBuildAsync(p, force: false, ct).ConfigureAwait(false);
        var options = ReadExportOptions(p);
        var selected = DeckExporter.Select(build.Result.Words, options);
        int limit = Math.Clamp(Int(p, "limit") ?? 60, 1, 500);

        return new
        {
            selectedCount = selected.Count,
            selectedOccurrences = selected.Sum(w => (long)w.Occurrences),
            totalOccurrences = build.Result.Words.Sum(w => (long)w.Occurrences),
            words = selected.Take(limit).Select(w => new
            {
                word = w.Word,
                reading = w.ReadingKana,
                furigana = w.ReadingFurigana,
                occurrences = w.Occurrences,
                frequencyRank = w.FrequencyRank,
                definitions = w.Definitions,
                example = w.ExampleSentence,
                resolved = w.ResolvedInDictionary,
                kind = w.Kind,
                usageNote = w.UsageNote
            })
        };
    }

    private async Task<object?> ExportDeckAsync(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        var detail = _catalog.GetBookDetail(bookId) ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");
        var build = await GetOrBuildAsync(p, force: false, ct).ConfigureAwait(false);
        var options = ReadExportOptions(p);

        string folder = detail.Summary.FolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new InvalidOperationException("The book has no folder on disk to attach a deck to.");

        string extension = options.Format == DeckFormat.Csv ? ".csv" : ".txt";
        string path = Path.Combine(folder, "deck" + extension);
        var enrichment = options.Format == DeckFormat.Csv && (Bool(p, "includeGrammar") ?? true)
            ? _store.Load(bookId, BookStamp(bookId))
            : null;
        string grammarPath = Path.Combine(folder, DeckGrammarJson.FileName);

        bool overwrite = Bool(p, "overwrite") ?? false;
        if ((File.Exists(path) || (enrichment is not null && File.Exists(grammarPath))) && !overwrite)
            return new { ok = false, exists = true, path = File.Exists(path) ? path : grammarPath };

        string content = DeckExporter.Render(build.Result.Words, options);
        await WriteAtomicAsync(path, content, ct).ConfigureAwait(false);
        if (enrichment is not null)
            await WriteAtomicAsync(grammarPath, DeckGrammarJson.Render(enrichment, detail.Summary.Title), ct).ConfigureAwait(false);

        var selected = DeckExporter.Select(build.Result.Words, options);
        return new
        {
            ok = true,
            exists = false,
            path,
            fileName = Path.GetFileName(path),
            wordCount = selected.Count,
            functionWordCount = selected.Count(w => DeckKinds.IsFunctionWord(w.Kind)),
            grammarPointCount = enrichment?.Points.Count ?? 0,
            grammarFile = enrichment is null ? null : DeckGrammarJson.FileName,
            fileSizeBytes = new FileInfo(path).Length
        };
    }

    /// <summary>Atomic replace so a crash never leaves a half-written file that the pipeline would reject.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- LLM enrichment RPC

    private Task<object?> EstimateEnrichment(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        var chapters = LoadChapterSentences(bookId);
        var chunks = new DeckEnricher(new NullLlm(), EnrichOptions()).Chunk(chapters.Where(c => c.Sentences.Count > 0).ToList());
        long chars = chunks.Sum(c => c.Sentences.Sum(s => (long)s.Length + 6));
        var conn = Connection();
        var stored = _store.Load(bookId, BookStamp(bookId));
        var limits = EffectiveLimits(_settings);

        return Task.FromResult<object?>(new
        {
            chapters = chapters.Count(c => c.Sentences.Count > 0),
            requests = chunks.Count,
            characters = chars,
            // Japanese runs at roughly 1 to 1.5 tokens per character; prompts add about a thousand per request.
            approxInputTokens = (long)(chars * 1.3) + chunks.Count * 1200L,
            configured = IsConfigured(conn),
            endpointHost = HostOf(conn.EndpointUrl),
            model = conn.ModelId,
            source = UsesOwnEndpoint(_settings) ? "plugin" : "settings",
            charsPerRequest = limits.ChunkChars,
            maxAnswerTokens = limits.MaxOutputTokens,
            existing = stored is null ? null : new
            {
                model = stored.Model,
                generatedAtUtc = stored.GeneratedAtUtc,
                grammarPoints = stored.Points.Count,
                functionWordNotes = stored.FunctionUses.Count,
                chunksFailed = stored.ChunksFailed,
                chunksTotal = stored.ChunksTotal
            }
        });
    }

    private async Task<object?> StartEnrichmentAsync(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        if (_jobs.TryGetValue(bookId, out var running) && running.State == "running")
            return new { started = false, reason = "already running" };
        if (!IsConfigured(Connection()))
            throw new InvalidOperationException("No LLM is configured. Set the LLM endpoint in the Deck Builder plugin settings, or the endpoint URL, model and key in Settings → AI.");

        // Function words come from a full build (a "content words only" deck has none).
        using var request = JsonDocument.Parse($"{{\"bookId\":{JsonSerializer.Serialize(bookId)},\"contentWordsOnly\":false}}");
        var build = await GetOrBuildAsync(request.RootElement.Clone(), force: false, ct).ConfigureAwait(false);
        var functionWords = build.Result.Words
            .Where(w => DeckKinds.IsFunctionWord(w.Kind))
            .OrderByDescending(w => w.Occurrences)
            .Take(80)
            .Select(w => w.Word)
            .ToList();

        var chapters = LoadChapterSentences(bookId);
        var job = new EnrichJob();
        _jobs[bookId] = job;
        string stamp = BookStamp(bookId);
        var enricher = new DeckEnricher(
            new OpenAiChatClient(_http, Connection, TimeSpan.FromSeconds(Math.Max(10, _settings.LlmRequestTimeoutSeconds)),
                EffectiveLimits(_settings).MaxOutputTokens, _settings.LlmTemperature),
            EnrichOptions());
        job.Total = enricher.Chunk(chapters.Where(c => c.Sentences.Count > 0).ToList()).Count;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await enricher.RunAsync(chapters, functionWords,
                    pr =>
                    {
                        job.Done = pr.Done; job.Total = pr.Total; job.Chapter = pr.Chapter;
                        job.Points = pr.GrammarPoints; job.FunctionWords = pr.FunctionWords; job.ChunksFailed = pr.ChunksFailed;
                        job.WarningCount = pr.WarningCount; job.RecentWarnings = pr.RecentWarnings ?? [];
                    }, job.Cts.Token).ConfigureAwait(false);
                result.BookStamp = stamp;

                if (result.ChunksTotal > 0 && result.ChunksFailed == result.ChunksTotal)
                    throw new DeckLlmException("Every request failed: " + (result.Warnings.FirstOrDefault() ?? "unknown error"));

                _store.Save(bookId, result);
                ApplyUsageNotes(_cache?.Result, result);
                job.Result = result;
                job.State = "done";
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
            }
            catch (Exception ex)
            {
                job.Error = ex.Message;
                job.State = "failed";
            }
        });

        return new { started = true, requests = job.Total, functionWords = functionWords.Count };
    }

    private Task<object?> GetEnrichmentStatus(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        var stored = _store.Load(bookId, BookStamp(bookId));
        _jobs.TryGetValue(bookId, out var job);
        bool running = job?.State == "running";
        // while a job runs the numbers are the job's live ones, never the previous run's (that made a re-run look unchanged)
        var result = running ? null : job?.Result ?? stored;
        int limit = Math.Clamp(Int(p, "limit") ?? 40, 1, 200);

        return Task.FromResult<object?>(new
        {
            state = job?.State ?? (stored is null ? "idle" : "done"),
            done = job?.Done ?? 0,
            total = job?.Total ?? 0,
            chapter = job?.Chapter ?? "",
            error = job?.Error,
            model = result?.Model,
            generatedAtUtc = result?.GeneratedAtUtc,
            warnings = running ? job!.RecentWarnings : result?.Warnings.Take(10),
            warningCount = running ? job!.WarningCount : result?.Warnings.Count ?? 0,
            chunksFailed = running ? job!.ChunksFailed : result?.ChunksFailed ?? 0,
            grammarPointCount = running ? job!.Points : result?.Points.Count ?? 0,
            functionWordNoteCount = running ? job!.FunctionWords : result?.FunctionUses.Count ?? 0,
            points = result?.Points.Take(limit).Select(g => new
            {
                id = g.Id,
                pattern = g.Pattern,
                category = g.Category,
                meaning = g.Meaning,
                explanation = g.Explanation,
                chapter = g.FirstChapterTitle,
                examples = g.Examples.Select(e => new { text = e.Text, surface = e.Surface })
            }),
            functionUses = result?.FunctionUses.Take(60).Select(kv => new
            {
                word = kv.Key,
                uses = kv.Value.Select(u => new { function = u.Function, note = u.Note, example = u.Example })
            })
        });
    }

    private Task<object?> CancelEnrichment(JsonElement p, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        if (_jobs.TryGetValue(bookId, out var job) && job.State == "running") job.Cts.Cancel();
        return Task.FromResult<object?>(new { ok = true });
    }

    private DeckEnrichmentOptions EnrichOptions()
    {
        var limits = EffectiveLimits(_settings);
        return new DeckEnrichmentOptions(MaxChunkChars: limits.ChunkChars, MaxPointsPerChunk: limits.MaxPoints);
    }

    /// <summary>Request sizes after applying what the server really accepts (<see cref="DeckBuilderSettings.LlmContextTokens"/>).</summary>
    internal readonly record struct LlmLimits(int ChunkChars, int MaxOutputTokens, int MaxPoints);

    /// <summary>
    /// Without a known context size the user's numbers are used as they are. With one (tokens per request, prompt plus answer) the answer
    /// gets at most 40 % of it, the prompt overhead is about 1,500 tokens and Japanese costs up to 1.5 tokens per character, so a server
    /// that advertises 262,144 tokens but enforces 8,241 still gets requests that fit (measured, see spec 18 section 7).
    /// </summary>
    internal static LlmLimits EffectiveLimits(DeckBuilderSettings s)
    {
        int chunk = Math.Clamp(s.LlmMaxChunkChars, 4_000, 400_000);
        int output = Math.Clamp(s.LlmMaxOutputTokens, 1_000, 64_000);
        int points = Math.Clamp(s.LlmMaxPointsPerChunk, 3, 100);
        if (s.LlmContextTokens > 0)
        {
            int context = Math.Max(2_000, s.LlmContextTokens);
            output = Math.Min(output, (int)(context * 0.4));
            chunk = Math.Clamp(Math.Min(chunk, (int)((context - output - 1_500) / 1.5)), 600, chunk);
            points = Math.Clamp(Math.Min(points, output / 200), 3, points);
        }
        return new LlmLimits(chunk, output, points);
    }

    internal static bool UsesOwnEndpoint(DeckBuilderSettings s) => !string.IsNullOrWhiteSpace(s.LlmEndpointUrl);

    /// <summary>The plugin's own endpoint when set, otherwise Settings → AI.</summary>
    internal static DeckLlmConnection ResolveConnection(DeckBuilderSettings s, LlmSettings appLlm)
    {
        if (UsesOwnEndpoint(s))
        {
            return new DeckLlmConnection(ChatCompletionsUrl(s.LlmEndpointUrl), s.LlmModelId.Trim(),
                string.IsNullOrWhiteSpace(s.LlmApiKey) ? null : s.LlmApiKey.Trim(),
                ModelOptional: true, DisableThinking: string.Equals(s.LlmThinking, "off", StringComparison.OrdinalIgnoreCase));
        }
        return new DeckLlmConnection(ChatCompletionsUrl(appLlm.EndpointUrl), appLlm.ModelId, string.IsNullOrWhiteSpace(appLlm.ApiKey) ? null : appLlm.ApiKey);
    }

    /// <summary>"http://host:1919/v1" becomes ".../v1/chat/completions"; a full chat-completions URL (or any other exact URL) is kept.</summary>
    internal static string ChatCompletionsUrl(string url)
    {
        string trimmed = (url ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("offline://", StringComparison.OrdinalIgnoreCase)) return trimmed;
        string noSlash = trimmed.TrimEnd('/');
        if (noSlash.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return noSlash;
        // a bare host (http://localhost:8091) is the common way servers are written down: it gets the standard /v1 base
        if (Uri.TryCreate(noSlash, UriKind.Absolute, out var host) && host.Scheme is "http" or "https" && host.AbsolutePath.TrimEnd('/').Length == 0)
            return ImmersiveReading.Core.Llm.LlmEndpoint.ChatCompletionsUrl(noSlash);
        return Uri.TryCreate(noSlash, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? noSlash + "/chat/completions"
            : trimmed;
    }

    private DeckLlmConnection Connection() => ResolveConnection(_settings, appConfig.CurrentConfig.Llm);

    private static bool IsConfigured(DeckLlmConnection c)
        => !string.IsNullOrWhiteSpace(c.EndpointUrl)
           && !c.EndpointUrl.StartsWith("offline://", StringComparison.OrdinalIgnoreCase)
           && (c.ModelOptional || !string.IsNullOrWhiteSpace(c.ModelId));

    /// <summary>Settings button "Test connection": lists the models the endpoint serves (GET .../models).</summary>
    private Task<object?> TestEndpointAsync(JsonElement p, CancellationToken ct) => ProbeEndpointAsync(_http, Connection(), ct);

    internal static async Task<object?> ProbeEndpointAsync(HttpClient http, DeckLlmConnection conn, CancellationToken ct)
    {
        if (!IsConfigured(conn))
            return new { ok = false, error = "No LLM endpoint is configured (plugin setting or Settings → AI)." };

        string chat = conn.EndpointUrl.TrimEnd('/');
        string baseUrl = chat.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? chat[..^"/chat/completions".Length] : chat;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var bare) && bare.AbsolutePath.TrimEnd('/').Length == 0) baseUrl += "/v1";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (!string.IsNullOrWhiteSpace(conn.ApiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", conn.ApiKey);
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new { ok = false, error = $"The endpoint answered HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}" };

            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            var models = data.Select(m => m.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "").Where(id => id.Length > 0).Take(12).ToList();
            int advertised = data.Select(m => m.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0).FirstOrDefault();
            return new
            {
                ok = true, latencyMs = (int)watch.ElapsedMilliseconds, models, advertisedContext = advertised,
                note = advertised > 0 ? "Servers often enforce less per request than they advertise: set \"LLM: context tokens\" to the real limit." : ""
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            return new { ok = false, error = $"Could not reach the endpoint: {ex.Message}" };
        }
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";

    private string BookStamp(string bookId)
    {
        string? epub = _catalog.GetEpubFilePath(bookId);
        return epub is not null && File.Exists(epub) ? File.GetLastWriteTimeUtc(epub).Ticks.ToString() : "";
    }

    /// <summary>The stored enrichment's usage notes go onto the matching particle / auxiliary words of a build.</summary>
    private static void ApplyUsageNotes(DeckBuildResult? build, EnrichmentResult? enrichment)
    {
        if (build is null || enrichment is null) return;
        foreach (var word in build.Words.Where(w => DeckKinds.IsFunctionWord(w.Kind)))
        {
            word.UsageNote = enrichment.FunctionUses.TryGetValue(word.Word, out var uses) && uses.Count > 0
                ? DeckEnricher.ToUsageNote(uses)
                : "";
        }
    }

    /// <summary>Sentences per chapter (each spine file belongs to the first chapter that references it).</summary>
    private IReadOnlyList<ChapterSentences> LoadChapterSentences(string bookId)
    {
        var detail = _catalog.GetBookDetail(bookId) ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");
        string epub = _catalog.GetEpubFilePath(bookId) ?? throw new FileNotFoundException("The book's EPUB file is not available.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChapterSentences>();

        foreach (var chapter in detail.Chapters.Where(c => !c.IsExcluded))
        {
            var hrefs = (chapter.MemberSpineHrefs is { Count: > 0 } m ? m : [chapter.SpineHref]).Where(seen.Add).ToList();
            var sentences = BookTextExtractor.ExtractParagraphs(epub, hrefs).SelectMany(SentenceSplitter.Split).ToList();
            result.Add(new ChapterSentences(chapter.ChapterIndex, chapter.Title, sentences));
        }
        return result;
    }

    private sealed class NullLlm : IDeckLlm
    {
        public string ModelId => "";
        public Task<JsonDocument> CompleteJsonAsync(string system, string user, CancellationToken ct) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- build

    private async Task<CachedBuild> GetOrBuildAsync(JsonElement p, bool force, CancellationToken ct)
    {
        string bookId = RequiredString(p, "bookId");
        var detail = _catalog.GetBookDetail(bookId) ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");
        string epub = _catalog.GetEpubFilePath(bookId) ?? throw new FileNotFoundException("The book's EPUB file is not available.");

        var chapterFilter = ReadIntSet(p, "chapterIndexes");
        bool contentOnly = Bool(p, "contentWordsOnly") ?? _settings.ContentWordsOnly;
        string key = $"{bookId}|{File.GetLastWriteTimeUtc(epub).Ticks}|{contentOnly}|{(chapterFilter is null ? "" : string.Join(",", chapterFilter.OrderBy(i => i)))}";

        await _buildLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _cache is { } cached && cached.Key == key) return cached;

            var hrefs = detail.Chapters
                .Where(c => !c.IsExcluded && (chapterFilter is null || chapterFilter.Contains(c.ChapterIndex)))
                .SelectMany(c => c.MemberSpineHrefs is { Count: > 0 } members ? members : [c.SpineHref])
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var result = await Task.Run(() => Build(epub, hrefs, detail.Summary.Language, contentOnly, ct), ct).ConfigureAwait(false);
            ApplyUsageNotes(result, _store.Load(bookId, BookStamp(bookId)));
            _cache = new CachedBuild(key, result);
            return _cache;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private DeckBuildResult Build(string epubPath, IReadOnlyList<string> spineHrefs, string? language, bool contentOnly, CancellationToken ct)
    {
        var paragraphs = BookTextExtractor.ExtractParagraphs(epubPath, spineHrefs);
        var plugin = languages.Resolve(string.IsNullOrWhiteSpace(language) ? "ja" : language);
        using var tokenizer = plugin.CreateTokenizer();
        var aligner = plugin.GetRubyAligner();

        string Furigana(string word, string reading)
        {
            if (aligner is null || !word.Any(SentenceSplitter.IsKanji)) return "";
            try
            {
                var sb = new StringBuilder();
                foreach (var unit in aligner.AlignRuby(word, reading, []))
                {
                    if (!string.IsNullOrEmpty(unit.Reading) && unit.Reading != unit.BaseText)
                        sb.Append(unit.BaseText).Append('[').Append(unit.Reading).Append(']');
                    else
                        sb.Append(unit.BaseText);
                }
                return sb.ToString();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return "";
            }
        }

        var parser = new DeckParser(
            tokenize: sentence => tokenizer.TokenizeParagraph(sentence.AsSpan(), []),
            dictionary: new SqliteDeckDictionary(dictionaries),
            furigana: Furigana);
        return parser.Build(paragraphs, new DeckBuildOptions(contentOnly), ct);
    }

    // ---------------------------------------------------------------- payload helpers

    private DeckExportOptions ReadExportOptions(JsonElement p) => new(
        Format: (Str(p, "format") ?? "csv").ToLowerInvariant() switch
        {
            "txt" => DeckFormat.Txt,
            "txtrepeated" => DeckFormat.TxtRepeated,
            _ => DeckFormat.Csv
        },
        Order: (Str(p, "order") ?? _settings.Order).ToLowerInvariant() switch
        {
            "deckfrequency" => DeckOrder.DeckFrequency,
            "globalfrequency" => DeckOrder.GlobalFrequency,
            _ => DeckOrder.Chronological
        },
        Selection: (Str(p, "selection") ?? "full").ToLowerInvariant() switch
        {
            "topn" => DeckDownloadType.TopN,
            "targetcoverage" => DeckDownloadType.TargetCoverage,
            "minoccurrences" => DeckDownloadType.MinOccurrences,
            _ => DeckDownloadType.Full
        },
        SelectionValue: Dbl(p, "selectionValue") ?? 0,
        ExcludeKana: Bool(p, "excludeKana") ?? _settings.ExcludeKana,
        ExcludeExampleSentences: Bool(p, "excludeExampleSentences") ?? _settings.ExcludeExampleSentences,
        ExcludeFunctionWords: Bool(p, "excludeFunctionWords") ?? _settings.ExcludeFunctionWords);

    private static string RequiredString(JsonElement p, string name)
        => Str(p, name) is { Length: > 0 } s ? s : throw new ArgumentException($"{name} is required.");

    private static string? Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? Dbl(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }

    private static HashSet<int>? ReadIntSet(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var set = new HashSet<int>();
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number) set.Add(e.GetInt32());
        return set.Count == 0 ? null : set;
    }
}

public sealed class DeckBuilderModule : ImmersiveReading.Core.Extensions.IPluginModule
{
    public string ModuleId => DeckBuilderPlugin.PluginIdValue;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new DeckBuilderPlugin(
            sp.GetRequiredService<ILanguagePluginResolver>(),
            sp.GetRequiredService<DictionaryDatabase>(),
            sp.GetRequiredService<IConfigurationManager>()));
    }

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
        => [provider.GetRequiredService<DeckBuilderPlugin>()];

    public void Dispose()
    {
    }
}
