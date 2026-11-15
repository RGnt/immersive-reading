using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Core.Prompts;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <summary>
/// Builds and browses the spoiler-gated character knowledge graph of a book or series from the library and reader UI
/// (specs 18 and 20). The graph is built once, chapter by chapter, by the separate Python project <c>knowledge-graph</c>; this
/// plugin exports the book text, runs the builder, follows its progress and reads the finished snapshots. No gating logic lives
/// here: the reader's position only selects which precomputed snapshot is shown.
/// </summary>
public sealed class CharacterKnowledgePlugin(IConfigurationManager appConfig) : IImmersivePlugin, IDisposable
{
    public const string PluginIdValue = "characterKnowledge";

    public PluginManifest Manifest { get; } = new()
    {
        Id = PluginIdValue,
        Name = "Character Knowledge",
        Version = "1.0.0",
        Author = "RGnt",
        Description = "Build a spoiler-safe knowledge graph of the characters of a book or series with a large language model, and browse who they are as of the chapter you are reading.",
        ReloadBehavior = PluginReloadBehavior.Hot,
        UiEntry = "ui.js",
        StyleEntry = "ui.css"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private IHostLibrary _library = null!;
    private IBookCatalog _catalog = null!;
    private string _dataDir = "";
    private CharacterKnowledgeSettings _settings = new();
    private IDisposable? _configSub;
    private IPluginHostContext? _context;
    private IDisposable? _promptRegistration;
    private SeriesResolver _resolver = null!;
    private readonly ConcurrentDictionary<string, BuildJob> _jobs = new();

    public ValueTask OnEnableAsync(IPluginHostContext context, CancellationToken ct = default)
    {
        _library = context.Library;
        _catalog = context.BookCatalog;
        _dataDir = context.PluginDataDirectory;
        _settings = context.Config.Get<CharacterKnowledgeSettings>();
        _context = context;
        _configSub = context.Config.OnConfigChanged<CharacterKnowledgeSettings>(s => { _settings = s; RegisterBuilderPrompts(); });
        RegisterBuilderPrompts();
        _resolver = new SeriesResolver(_library, LoadVolumeMap);
        // other features (persona chat, a tutor ...) reach the story graph through the knowledge hub, always from a reader's viewpoint
        context.RegisterKnowledgeSource(new StoryKnowledgeSource(_resolver.ResolveForBook, Db));

        context.RegisterRpcAction("getStatus", GetStatus);
        context.RegisterRpcAction("testEndpoint", TestEndpointAsync);
        context.RegisterRpcAction("estimateBuild", EstimateBuild);
        context.RegisterRpcAction("startBuild", StartBuild);
        context.RegisterRpcAction("getBuildStatus", GetBuildStatus);
        context.RegisterRpcAction("cancelBuild", CancelBuild);
        context.RegisterRpcAction("listCharacters", ListCharacters);
        context.RegisterRpcAction("getProfile", GetProfile);
        context.RegisterRpcAction("listEvents", ListEvents);
        context.RegisterRpcAction("listItems", ListItems);
        context.RegisterRpcAction("deleteKnowledge", DeleteKnowledge);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnDisableAsync(CancellationToken ct = default)
    {
        _configSub?.Dispose();
        _configSub = null;
        _promptRegistration?.Dispose();
        _promptRegistration = null;
        foreach (var job in _jobs.Values) KnowledgeBuildRunner.Cancel(job);   // a chapter in flight is lost, finished chapters are kept
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => OnDisableAsync();
    public void Dispose() => _configSub?.Dispose();

    // ------------------------------------------------------------------ prompts (spec 19, PT-5)
    /// <summary>
    /// The builder's prompt files are the single source of the built-in prompts: they are read from the builder folder and registered with
    /// the host, so the user can edit them in Settings -> Prompts. Nothing is registered when the builder is not found (nothing to edit yet).
    /// </summary>
    private void RegisterBuilderPrompts()
    {
        _promptRegistration?.Dispose();
        _promptRegistration = null;
        string? directory = BuilderLocator.Locate(_settings).Directory;
        string promptsDir = directory is null ? "" : Path.Combine(directory, "immersive_knowledge", "prompts");
        if (_context is null || !Directory.Exists(promptsDir)) return;
        try
        {
            var texts = Directory.GetFiles(promptsDir, "*.prompt.md").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();
            if (texts.Count > 0) _promptRegistration = _context.RegisterPrompts(texts);
        }
        catch (Exception ex) when (ex is IOException or PromptTemplateException or InvalidOperationException)
        {
            Trace.TraceWarning($"Character Knowledge prompts could not be registered: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ paths and volume map
    private string SeriesDir(string key) => Path.Combine(_dataDir, "knowledge", key);
    private KnowledgeDb Db(string key) => new(Path.Combine(SeriesDir(key), "knowledge.db"));

    private IReadOnlyDictionary<string, int>? LoadVolumeMap(string key)
    {
        string path = Path.Combine(SeriesDir(key), "volumes.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("volumes").EnumerateArray()
                .ToDictionary(v => v.GetProperty("bookId").GetString()!, v => v.GetProperty("volume").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IOException)
        {
            return null;
        }
    }

    private void SaveVolumeMap(SeriesInfo series)
    {
        Directory.CreateDirectory(SeriesDir(series.SeriesKey));
        var json = new JsonObject
        {
            ["seriesKey"] = series.SeriesKey,
            ["volumes"] = new JsonArray(series.Volumes.Select(v => (JsonNode)new JsonObject { ["bookId"] = v.BookId, ["volume"] = v.Volume }).ToArray())
        };
        File.WriteAllText(Path.Combine(SeriesDir(series.SeriesKey), "volumes.json"), json.ToJsonString());
    }

    private SeriesInfo ResolveSeries(JsonElement p)
    {
        string? seriesName = Str(p, "seriesName");
        string? bookId = Str(p, "bookId");
        if (!string.IsNullOrWhiteSpace(bookId)) return _resolver.ResolveForBook(bookId);
        if (!string.IsNullOrWhiteSpace(seriesName)) return _resolver.ResolveForSeries(seriesName);
        throw new ArgumentException("bookId or seriesName is required.");
    }

    // ------------------------------------------------------------------ model configuration
    /// <summary>The model that builds the graph: this plugin's endpoint, or Settings → AI when it is left empty.</summary>
    internal (string Endpoint, string Model, string? Key) EffectiveLlm()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EndpointUrl))
            return (_settings.EndpointUrl.Trim(), _settings.ModelId.Trim(), string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey);
        var llm = appConfig.CurrentConfig.Llm;
        return (llm.EndpointUrl, llm.ModelId, string.IsNullOrWhiteSpace(llm.ApiKey) ? null : llm.ApiKey);
    }

    private static bool IsUsable(string endpoint) => !string.IsNullOrWhiteSpace(endpoint) && !endpoint.StartsWith("offline://", StringComparison.OrdinalIgnoreCase);

    internal JsonObject BuilderConfig()
    {
        var (endpoint, model, _) = EffectiveLlm();      // the key is deliberately NOT part of the config file
        var config = new JsonObject
        {
            ["endpoint"] = endpoint,
            ["model"] = model,
            ["output_language"] = string.IsNullOrWhiteSpace(_settings.OutputLanguage) ? "Japanese" : _settings.OutputLanguage,
            ["thinking"] = _settings.Thinking is "on" or "default" ? _settings.Thinking : "off",
            ["max_input_chars"] = Math.Clamp(_settings.MaxInputChars, 1000, 500_000),
            ["timeout_seconds"] = Math.Clamp(_settings.RequestTimeoutSeconds, 30, 7200),
            ["min_prominence"] = Math.Max(1, _settings.MinProminence),
            ["story_pass"] = _settings.ExtractStory            // events and item holdings (spec 22)
        };
        if (_settings.ContextTokens > 0) config["context_tokens"] = _settings.ContextTokens;
        return config;
    }

    // ------------------------------------------------------------------ status
    private Task<object?> GetStatus(JsonElement p, CancellationToken ct)
    {
        var series = ResolveSeries(p);
        string? bookId = Str(p, "bookId");
        var status = Db(series.SeriesKey).GetStatus();
        var builder = BuilderLocator.Locate(_settings);
        var (endpoint, model, _) = EffectiveLlm();
        _jobs.TryGetValue(series.SeriesKey, out var job);

        return Task.FromResult<object?>(new
        {
            series = new
            {
                key = series.SeriesKey,
                title = series.Title,
                isSeries = series.IsSeries,
                orderChanged = series.OrderChanged,
                volumes = series.Volumes.Select(v =>
                {
                    var rows = status.Chapters.Where(c => c.Volume == v.Volume).ToList();
                    var story = status.StoryChapters.Where(c => c.Volume == v.Volume).ToList();
                    return new
                    {
                        volume = v.Volume, bookId = v.BookId, title = v.Title, seriesIndex = v.SeriesIndex, chapterCount = v.ChapterCount,
                        built = rows.Count(c => c.Status == "built"), failed = rows.Count(c => c.Status == "failed"),
                        storyBuilt = story.Count(c => c.Status == "built"), storyFailed = story.Count(c => c.Status == "failed")
                    };
                })
            },
            currentVolume = bookId is null ? (int?)null : series.VolumeOf(bookId),
            readingChapterIndex = bookId is null ? null : _library.GetReadingChapterIndex(bookId),
            builder = new { found = builder.Found, problem = builder.Problem, directory = builder.Directory },
            llm = new { configured = IsUsable(endpoint), endpoint = HostOf(endpoint), model, usesSettingsAi = string.IsNullOrWhiteSpace(_settings.EndpointUrl) },
            knowledge = new
            {
                exists = status.Chapters.Count > 0,
                counts = status.Counts,
                failedChapters = status.Chapters.Where(c => c.Status == "failed").Select(c => new { c.Volume, c.Chapter, c.Title, c.Error }),
                extractStory = _settings.ExtractStory,
                storyBuilt = status.StoryChapters.Count(c => c.Status == "built"),
                hasStory = status.Counts.EventCards > 0 || status.Counts.ItemCards > 0,
                failedStoryChapters = status.StoryChapters.Where(c => c.Status == "failed").Select(c => new { c.Volume, c.Chapter, c.Error })
            },
            job = job is null ? null : JobDto(job)
        });
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url;

    private static object JobDto(BuildJob job) => new
    {
        state = job.State,
        total = job.Total,
        done = job.Done,
        skipped = job.Skipped,
        phase = job.Phase,
        currentVolume = job.CurrentVolume,
        currentChapter = job.CurrentChapter,
        currentTitle = job.CurrentTitle,
        opsApplied = job.OpsApplied,
        opsRejected = job.OpsRejected,
        storyOpsApplied = job.StoryOpsApplied,
        storyOpsRejected = job.StoryOpsRejected,
        eventCards = job.EventCards,
        itemCards = job.ItemCards,
        splits = job.Splits,
        contextTokensLearned = job.ContextTokensLearned,
        snapshotsStored = job.SnapshotsStored,
        snapshotsBlocked = job.SnapshotsBlocked,
        model = job.Model,
        error = job.Error,
        exitCode = job.ExitCode,
        startedAtUtc = job.StartedAtUtc,
        finishedAtUtc = job.FinishedAtUtc,
        log = job.Log.ToArray()
    };

    private async Task<object?> TestEndpointAsync(JsonElement p, CancellationToken ct)
    {
        var (endpoint, _, key) = EffectiveLlm();
        if (!IsUsable(endpoint)) return new { ok = false, error = "No endpoint is configured (plugin settings or Settings → AI)." };
        string baseUrl = endpoint.TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^"/chat/completions".Length];

        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
            if (key is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return new { ok = false, error = $"The endpoint answered HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}" };

            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
            string model = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            int advertised = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number ? ctx.GetInt32() : 0;
            return new
            {
                ok = true, latencyMs = (int)watch.ElapsedMilliseconds, model, advertisedContext = advertised,
                note = advertised > 0 ? "The advertised context is not always what the server enforces per request; the builder discovers the real limit." : ""
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return new { ok = false, error = $"Could not reach the endpoint: {ex.Message}" };
        }
    }

    // ------------------------------------------------------------------ estimate and build
    private (SeriesInfo Series, int Upto) Scope(JsonElement p)
    {
        var series = ResolveSeries(p);
        string scope = Str(p, "scope") ?? "upToVolume";
        string? bookId = Str(p, "bookId");
        int upto = scope == "series" || bookId is null ? int.MaxValue : series.VolumeOf(bookId);
        return (series, upto);
    }

    private int PartChars()
    {
        int max = Math.Clamp(_settings.MaxInputChars, 1000, 500_000);
        return _settings.ContextTokens > 0 ? Math.Max(400, Math.Min(max, (int)(0.25 * _settings.ContextTokens * 1.6))) : max;
    }

    /// <summary>Chapters the builder would skip: built, text unchanged, and every earlier chapter also unchanged (it rebuilds from the first change on).</summary>
    internal static int UpToDateChapters(KnowledgeStatus status, ExportedBook exported)
    {
        var rows = status.Chapters.ToDictionary(c => (c.Volume, c.Chapter));
        int count = 0;
        foreach (var key in exported.ChapterHashes.Keys.OrderBy(k => k.Volume).ThenBy(k => k.Chapter))
        {
            if (rows.TryGetValue(key, out var row) && row.Status == "built" && row.TextSha == exported.ChapterHashes[key]) count++;
            else break;
        }
        return count;
    }

    /// <summary>
    /// Chapters whose story pass the builder would skip: the character pass of the chapter and every earlier one is current, and the
    /// story state of each is built for the same text. (The story pass depends on the character data of the same chapter.)
    /// </summary>
    internal static int UpToDateStoryChapters(KnowledgeStatus status, ExportedBook exported)
    {
        var characters = status.Chapters.ToDictionary(c => (c.Volume, c.Chapter));
        var story = status.StoryChapters.ToDictionary(c => (c.Volume, c.Chapter));
        int count = 0;
        foreach (var key in exported.ChapterHashes.Keys.OrderBy(k => k.Volume).ThenBy(k => k.Chapter))
        {
            string sha = exported.ChapterHashes[key];
            if (characters.TryGetValue(key, out var c) && c.Status == "built" && c.TextSha == sha
                && story.TryGetValue(key, out var s) && s.Status == "built" && s.TextSha == sha) count++;
            else break;
        }
        return count;
    }

    private Task<object?> EstimateBuild(JsonElement p, CancellationToken ct)
    {
        var (series, upto) = Scope(p);
        var exported = new BookTextExporter(_catalog).Export(series, upto);
        var status = Db(series.SeriesKey).GetStatus();
        int upToDate = UpToDateChapters(status, exported);
        int storyUpToDate = _settings.ExtractStory ? UpToDateStoryChapters(status, exported) : exported.Chapters;

        var texts = exported.Json["volumes"]!.AsArray()
            .SelectMany(v => v!["chapters"]!.AsArray().Select(c => (Volume: v["volume"]!.GetValue<int>(), Chapter: c!["index"]!.GetValue<int>(), Length: c["text"]!.GetValue<string>().Length)))
            .OrderBy(t => t.Volume).ThenBy(t => t.Chapter)
            .Skip(upToDate)
            .ToList();
        static int Requests(IEnumerable<(int V, int C, int Length)> items, int partChars) => items.Sum(t => t.Length == 0 ? 0 : (int)Math.Ceiling(t.Length / (double)partChars));
        var items = texts.Select(t => (t.Volume, t.Chapter, t.Length)).ToList();
        // the story pass starts at the first chapter whose story state is not current (it also redoes everything after it)
        var storyItems = exported.Json["volumes"]!.AsArray()
            .SelectMany(v => v!["chapters"]!.AsArray().Select(c => (Volume: v["volume"]!.GetValue<int>(), Chapter: c!["index"]!.GetValue<int>(), Length: c["text"]!.GetValue<string>().Length)))
            .OrderBy(t => t.Volume).ThenBy(t => t.Chapter)
            .Skip(storyUpToDate)
            .Select(t => (t.Volume, t.Chapter, t.Length))
            .ToList();
        var (endpoint, model, _) = EffectiveLlm();

        return Task.FromResult<object?>(new
        {
            volumes = series.Volumes.Count(v => v.Volume <= upto),
            chapters = exported.Chapters,
            characters = exported.Characters,
            alreadyBuilt = upToDate,
            toBuild = exported.Chapters - upToDate,
            extractStory = _settings.ExtractStory,
            storyAlreadyBuilt = _settings.ExtractStory ? storyUpToDate : 0,
            storyToBuild = _settings.ExtractStory ? exported.Chapters - storyUpToDate : 0,
            storyRequests = _settings.ExtractStory ? Requests(storyItems, PartChars()) : 0,
            requests = Requests(items, PartChars()),
            // Servers often enforce a far smaller context than they advertise (measured: 8,241 tokens); with that the text is cut into small parts.
            requestsIfSmallContext = Requests(items, (int)(0.25 * 8241 * 1.6)),
            contextKnown = _settings.ContextTokens > 0,
            configured = IsUsable(endpoint),
            endpoint = HostOf(endpoint),
            model
        });
    }

    private Task<object?> StartBuild(JsonElement p, CancellationToken ct)
    {
        var builder = BuilderLocator.Locate(_settings);
        if (!builder.Found) throw new InvalidOperationException(builder.Problem);
        var (endpoint, _, key) = EffectiveLlm();
        if (!IsUsable(endpoint)) throw new InvalidOperationException("No model endpoint is configured. Set one in the plugin settings or in Settings → AI.");
        if (_jobs.Values.Any(j => j.State == "running")) throw new InvalidOperationException("A knowledge build is already running. Wait for it or cancel it first.");

        var (series, upto) = Scope(p);
        var exported = new BookTextExporter(_catalog).Export(series, upto);
        if (exported.Characters == 0) throw new InvalidOperationException("The book has no text to read.");

        string dir = SeriesDir(series.SeriesKey);
        string jobDir = Path.Combine(dir, "job");
        Directory.CreateDirectory(jobDir);
        string textPath = Path.Combine(jobDir, "book-text.json");
        string configPath = Path.Combine(jobDir, "config.json");
        File.WriteAllText(textPath, exported.Json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        var builderConfig = BuilderConfig();
        // the prompts in force (the user's edited copies where they are valid) are handed to the builder as files; it never sees a half-valid copy
        if (_context is not null && _context.Prompts.WriteEffective(PluginIdValue, Path.Combine(jobDir, "prompts")) > 0)
            builderConfig["prompts_dir"] = Path.Combine(jobDir, "prompts");
        File.WriteAllText(configPath, builderConfig.ToJsonString(), new UTF8Encoding(false));
        SaveVolumeMap(series);

        var request = new BuildRequest(series.SeriesKey, Path.Combine(dir, "knowledge.db"), textPath, configPath, key, Bool(p, "rebuild") ?? false);
        // two passes over the chapters when the story pass is on: progress counts both
        var job = new KnowledgeBuildRunner(builder).Start(request, exported.Chapters * (_settings.ExtractStory ? 2 : 1));
        _jobs[series.SeriesKey] = job;
        return Task.FromResult<object?>(new { started = job.State == "running", error = job.Error, chapters = exported.Chapters, jobKey = series.SeriesKey });
    }

    private Task<object?> GetBuildStatus(JsonElement p, CancellationToken ct)
    {
        var series = ResolveSeries(p);
        return Task.FromResult<object?>(new { job = _jobs.TryGetValue(series.SeriesKey, out var job) ? JobDto(job) : null });
    }

    private Task<object?> CancelBuild(JsonElement p, CancellationToken ct)
    {
        var series = ResolveSeries(p);
        if (_jobs.TryGetValue(series.SeriesKey, out var job)) KnowledgeBuildRunner.Cancel(job);
        return Task.FromResult<object?>(new { ok = true });
    }

    // ------------------------------------------------------------------ browsing
    private (SeriesInfo Series, int Volume, int Chapter) Position(JsonElement p)
    {
        string bookId = Str(p, "bookId") ?? throw new ArgumentException("bookId is required.");
        var series = _resolver.ResolveForBook(bookId);
        int chapter = Int(p, "chapterIndex") ?? _library.GetReadingChapterIndex(bookId) ?? 0;
        return (series, series.VolumeOf(bookId), Math.Max(0, chapter));
    }

    private Task<object?> ListCharacters(JsonElement p, CancellationToken ct)
    {
        var (series, volume, chapter) = Position(p);
        var characters = Db(series.SeriesKey).ListCharacters(volume, chapter);
        return Task.FromResult<object?>(new { asOf = new { volume, chapter }, characters });
    }

    private Task<object?> GetProfile(JsonElement p, CancellationToken ct)
    {
        var (series, volume, chapter) = Position(p);
        string id = Str(p, "characterId") ?? throw new ArgumentException("characterId is required.");
        var profile = Db(series.SeriesKey).GetProfile(volume, chapter, id);
        return Task.FromResult<object?>(new { asOf = new { volume, chapter }, profile });
    }

    private Task<object?> ListEvents(JsonElement p, CancellationToken ct)
    {
        var (series, volume, chapter) = Position(p);
        int limit = Math.Clamp(Int(p, "limit") ?? 200, 1, 2000);
        var result = Db(series.SeriesKey).ListEvents(volume, chapter, Str(p, "characterId"), Str(p, "kind"), limit);
        return Task.FromResult<object?>(new { asOf = new { volume, chapter }, total = result.Total, events = result.Events });
    }

    private Task<object?> ListItems(JsonElement p, CancellationToken ct)
    {
        var (series, volume, chapter) = Position(p);
        var items = Db(series.SeriesKey).ListItems(volume, chapter, Str(p, "holderId"));
        return Task.FromResult<object?>(new { asOf = new { volume, chapter }, items });
    }

    private Task<object?> DeleteKnowledge(JsonElement p, CancellationToken ct)
    {
        var series = ResolveSeries(p);
        if (_jobs.TryGetValue(series.SeriesKey, out var job) && job.State == "running")
            throw new InvalidOperationException("Cancel the running build first.");
        string dir = SeriesDir(series.SeriesKey);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        _jobs.TryRemove(series.SeriesKey, out _);
        return Task.FromResult<object?>(new { ok = true });
    }

    // ------------------------------------------------------------------ payload helpers
    private static string? Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? Bool(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
}

public sealed class CharacterKnowledgeModule : IPluginModule
{
    public string ModuleId => CharacterKnowledgePlugin.PluginIdValue;

    public void ConfigureServices(IServiceCollection services)
        => services.AddSingleton(sp => new CharacterKnowledgePlugin(sp.GetRequiredService<IConfigurationManager>()));

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
        => [provider.GetRequiredService<CharacterKnowledgePlugin>()];

    public void Dispose()
    {
    }
}
