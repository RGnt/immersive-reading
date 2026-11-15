using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <param name="ApiKey">Handed to the builder through the <c>KNOWLEDGE_API_KEY</c> environment variable only (never on a command line, never logged).</param>
public sealed record BuildRequest(string SeriesKey, string DbPath, string BookTextPath, string ConfigPath, string? ApiKey, bool Rebuild = false, string? FromPosition = null);

/// <summary>Live state of one build; read by the UI through polling.</summary>
public sealed class BuildJob
{
    public required string SeriesKey { get; init; }
    public string State { get; set; } = "running";       // running | done | failed | cancelled
    public int Total { get; set; }
    public int Done { get; set; }
    public int Skipped { get; set; }
    public int OpsApplied { get; set; }
    public int OpsRejected { get; set; }
    public int StoryOpsApplied { get; set; }
    public int StoryOpsRejected { get; set; }
    public int EventCards { get; set; }
    public int ItemCards { get; set; }
    public string Phase { get; set; } = "characters";       // characters | story
    public int Splits { get; set; }
    public int ContextTokensLearned { get; set; }
    public int SnapshotsStored { get; set; }
    public int SnapshotsBlocked { get; set; }
    public string Model { get; set; } = "";
    public int CurrentVolume { get; set; }
    public int CurrentChapter { get; set; } = -1;
    public string CurrentTitle { get; set; } = "";
    public string? Error { get; set; }
    public int? ExitCode { get; set; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public ConcurrentQueue<string> Log { get; } = new();
    internal CancellationTokenSource Cancel { get; } = new();
    internal Process? Process { get; set; }
    internal bool CancelRequested { get; set; }

    public void AddLog(string line)
    {
        Log.Enqueue(line);
        while (Log.Count > 60) Log.TryDequeue(out _);
    }
}

/// <summary>Runs <c>python -m immersive_knowledge build</c> and follows its JSON-lines progress (spec 18, contract 6.3).</summary>
public sealed class KnowledgeBuildRunner(BuilderLocation location, Func<BuildRequest, ProcessStartInfo>? startInfoFactory = null)
{
    public BuildJob Start(BuildRequest request, int totalChapters)
    {
        if (!location.Found && startInfoFactory is null) throw new InvalidOperationException(location.Problem);

        var job = new BuildJob { SeriesKey = request.SeriesKey, Total = totalChapters };
        var info = (startInfoFactory ?? DefaultStartInfo)(request);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        job.Process = process;
        var stderr = new ConcurrentQueue<string>();

        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) Handle(job, e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 }) return;
            stderr.Enqueue(e.Data);
            while (stderr.Count > 12) stderr.TryDequeue(out string? _);
        };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            job.State = "failed";
            job.Error = $"The builder could not be started: {ex.Message}";
            job.FinishedAtUtc = DateTime.UtcNow;
            return job;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            process.WaitForExit();                    // flush the asynchronous readers
            job.ExitCode = process.ExitCode;
            job.FinishedAtUtc = DateTime.UtcNow;
            if (job.CancelRequested) job.State = "cancelled";
            else if (process.ExitCode == 0) job.State = "done";
            else
            {
                job.State = "failed";
                job.Error ??= string.Join(" ", stderr.TakeLast(3));
                if (string.IsNullOrWhiteSpace(job.Error)) job.Error = $"The builder stopped with exit code {process.ExitCode}.";
                if (process.ExitCode == 4) job.Error += " Already built chapters are kept; start again to continue from the failed chapter.";
            }
            process.Dispose();
        });
        return job;
    }

    public static void Cancel(BuildJob job)
    {
        if (job.State != "running") return;
        job.CancelRequested = true;
        try
        {
            job.Process?.Kill(entireProcessTree: true);   // chapters commit one at a time, so an interrupted build is resumable
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // already gone
        }
    }

    internal ProcessStartInfo DefaultStartInfo(BuildRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = location.PythonExe!,
            WorkingDirectory = location.Directory!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in new[] { "-m", "immersive_knowledge", "build", "--db", request.DbPath, "--book-text", request.BookTextPath, "--config", request.ConfigPath })
            info.ArgumentList.Add(arg);
        if (request.Rebuild) info.ArgumentList.Add("--no-resume");
        if (request.FromPosition is not null) { info.ArgumentList.Add("--from"); info.ArgumentList.Add(request.FromPosition); }
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        if (!string.IsNullOrWhiteSpace(request.ApiKey)) info.Environment["KNOWLEDGE_API_KEY"] = request.ApiKey;
        return info;
    }

    /// <summary>Applies one JSON-lines progress event to the job. Unknown or malformed lines are kept in the log, never fatal.</summary>
    internal static void Handle(BuildJob job, string line)
    {
        JsonElement e;
        try
        {
            using var doc = JsonDocument.Parse(line);
            e = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            job.AddLog(line.Length > 300 ? line[..300] : line);
            return;
        }
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("event", out var ev)) { job.AddLog(line); return; }

        int Int(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        switch (ev.GetString())
        {
            case "build_start":
                job.Model = Str("model");
                break;
            case "chapter_start":
                job.Phase = "characters";
                job.CurrentVolume = Int("volume");
                job.CurrentChapter = Int("chapter");
                job.CurrentTitle = Str("title");
                break;
            case "chapter_done":
                job.Done++;
                job.OpsApplied += Int("ops_applied");
                job.OpsRejected += Int("ops_rejected");
                job.AddLog($"chapter {Int("volume")}:{Int("chapter")} done: {Int("ops_applied")} facts, {Int("ops_rejected")} rejected");
                break;
            case "chapter_skipped":
                job.Done++;
                job.Skipped++;
                break;
            case "chapter_failed":
                job.Error = $"Chapter {Int("volume")}:{Int("chapter")} failed: {Str("reason")}";
                job.AddLog(job.Error);
                break;
            case "story_start":
                job.Phase = "story";
                job.CurrentVolume = Int("volume");
                job.CurrentChapter = Int("chapter");
                job.CurrentTitle = Str("title");
                break;
            case "story_done":
                job.Done++;
                job.StoryOpsApplied += Int("ops_applied");
                job.StoryOpsRejected += Int("ops_rejected");
                job.AddLog($"chapter {Int("volume")}:{Int("chapter")} story done: {Int("ops_applied")} facts, {Int("ops_rejected")} rejected");
                break;
            case "story_skipped":
                job.Done++;
                job.Skipped++;
                break;
            case "story_failed":
                job.Error = $"Story pass of chapter {Int("volume")}:{Int("chapter")} failed: {Str("reason")}";
                job.AddLog(job.Error);
                break;
            case "part_split":
                job.Splits++;
                job.AddLog($"chapter {Int("volume")}:{Int("chapter")}: the text was split in two to fit the model's limits");
                break;
            case "context_limit_learned":
                job.ContextTokensLearned = Int("tokens");
                job.AddLog($"the model server accepts at most {Int("tokens")} tokens per request; requests are sized accordingly");
                break;
            case "snapshot_shrunk":
                job.AddLog($"known-facts summary shrunk to level {Int("level")} to fit the model's limits");
                break;
            case "snapshots_done":
                job.SnapshotsStored = Int("stored");
                job.SnapshotsBlocked = Int("blocked") + Int("story_blocked");
                job.EventCards = Int("events");
                job.ItemCards = Int("items");
                break;
            case "error":
                job.Error = Str("message");
                job.AddLog(job.Error);
                break;
        }
    }
}
