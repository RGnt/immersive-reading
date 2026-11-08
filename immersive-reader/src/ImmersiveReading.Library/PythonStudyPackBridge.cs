using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Library.StudyPack;

namespace ImmersiveReading.Library;

/// <summary>
/// Facade over the study-pack components: publication reading, snapshot leases,
/// overview building, pluggable stage DAG execution, and the Python build runner.
/// </summary>
public sealed class PythonStudyPackBridge : IStudyPackCatalog
{
    private readonly string _outputRoot;
    private readonly string? _extractorRoot;
    private readonly string? _configuredPythonExe;
    private readonly IStudyPackBuilder _builder;
    private IPythonStageRegistryProvider? _stageRegistry;

    public string OutputRoot => _outputRoot;

    public IPythonStageRegistryProvider? StageRegistry
    {
        get => _stageRegistry;
        set => _stageRegistry = value;
    }

    public PythonStudyPackBridge(
        string outputRoot,
        string? extractorRoot = null,
        IStudyPackBuilder? builder = null,
        string? configuredPythonExe = null,
        IPythonStageRegistryProvider? stageRegistry = null)
    {
        _outputRoot = Path.GetFullPath(outputRoot);
        _extractorRoot = extractorRoot is not null ? Path.GetFullPath(extractorRoot) : null;
        _configuredPythonExe = configuredPythonExe;
        _builder = builder ?? new PythonBuildRunner(_outputRoot, extractorRoot, configuredPythonExe);
        _stageRegistry = stageRegistry;
    }

    public (string? Generation, string? ManifestPath, JsonDocument? ManifestDoc) TryReadActivePublication()
        => PublicationReader.TryReadActivePublication(_outputRoot);

    public StudyPackLease? AcquireSnapshotLease(string? generation = null)
        => SnapshotLeaseManager.AcquireSnapshotLease(_outputRoot, generation);

    public StudyPackBookOverview GetBookStudyPack(string bookId)
        => StudyPackOverviewBuilder.GetBookStudyPack(_outputRoot, bookId);

    public Task<StudyPackBuildResult> BuildStudyPackAsync(
        string bookId,
        string bookFolderPath,
        bool readerOnly = false,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
        => _builder.BuildStudyPackAsync(bookId, bookFolderPath, readerOnly, onProgressLine, ct);

    public async Task<StudyPackBuildResult> BuildStudyPackAsync(
        BookSummary book,
        string epubPath,
        StudyPackOptions? options = null,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
    {
        options ??= new StudyPackOptions();
        var lang = string.IsNullOrWhiteSpace(book.Language) ? "ja" : book.Language;

        if (_stageRegistry is not null)
        {
            var registrations = await _stageRegistry.ResolveStagesForLanguageAsync(lang, ct).ConfigureAwait(false);
            if (registrations.Count > 0)
            {
                return await BuildWithManifestStagesAsync(book, epubPath, registrations, options, onProgressLine, ct).ConfigureAwait(false);
            }
        }

        // Fallback to legacy builder if no stages registered for this language
        return await _builder.BuildStudyPackAsync(book.BookId, book.FolderPath, options.ReaderOnly, onProgressLine, ct).ConfigureAwait(false);
    }

    private async Task<StudyPackBuildResult> BuildWithManifestStagesAsync(
        BookSummary book,
        string epubPath,
        IReadOnlyList<ActivePythonStageRegistration> registrations,
        StudyPackOptions options,
        IProgress<string>? onProgressLine,
        CancellationToken ct)
    {
        string pythonExe = _configuredPythonExe ?? "";
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
        {
            var extRoot = _extractorRoot ?? Path.GetDirectoryName(_outputRoot) ?? "";
            var venvScript = Path.Combine(extRoot, ".venv", "Scripts", "python.exe");
            if (!File.Exists(venvScript))
                venvScript = Path.Combine(extRoot, ".venv", "bin", "python");
            pythonExe = File.Exists(venvScript) ? venvScript : "python";
        }

        var dataRootDir = Path.GetDirectoryName(_outputRoot) ?? _outputRoot;
        var sharedSitePackagesDir = Path.Combine(dataRootDir, "python-packages");

        var allPipReqs = registrations
            .SelectMany(r => r.Descriptor.PipRequirements)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allPipReqs.Count > 0)
        {
            await EnsurePipRequirementsAsync(pythonExe, sharedSitePackagesDir, allPipReqs, onProgressLine, ct).ConfigureAwait(false);
        }

        var outputDir = Path.Combine(_outputRoot, "data", book.BookId);
        Directory.CreateDirectory(outputDir);

        var pluginConfigs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var manifestStages = new List<ResolvedPythonStageManifestEntry>(registrations.Count);

        foreach (var reg in registrations)
        {
            pluginConfigs[reg.PluginId] = reg.PluginConfig;
            var scriptDiskPath = Path.Combine(
                reg.PluginDataDirectory,
                reg.Descriptor.ScriptRelativePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));

            manifestStages.Add(new ResolvedPythonStageManifestEntry
            {
                PluginId = reg.PluginId,
                StageId = reg.Descriptor.StageId,
                Order = reg.Descriptor.Order,
                ScriptFilePath = scriptDiskPath,
                EntryClassName = reg.Descriptor.EntryClassName,
                PluginDataDir = reg.PluginDataDirectory
            });
        }

        var extraPaths = new List<string> { sharedSitePackagesDir };
        if (!string.IsNullOrWhiteSpace(_extractorRoot))
        {
            extraPaths.Add(_extractorRoot);
        }

        var extDir = _extractorRoot ?? Path.GetDirectoryName(_outputRoot) ?? "";
        var dataDir = Path.Combine(extDir, "data");
        if (!Directory.Exists(dataDir))
        {
            var fallback = Path.Combine(Directory.GetCurrentDirectory(), "enrichement-extractor", "data");
            if (Directory.Exists(fallback)) dataDir = fallback;
        }
        var dictDbCandidate = Path.Combine(dataRootDir, "dictionaries.db");

        var manifest = new PythonPipelineRunManifest
        {
            BookId = book.BookId,
            Title = book.Title,
            Language = string.IsNullOrWhiteSpace(book.Language) ? "ja" : book.Language,
            EpubFilePath = epubPath,
            OutputDirectory = outputDir,
            DeckCsvPath = options.DeckCsvPath,
            DataDirectory = Directory.Exists(dataDir) ? dataDir : null,
            DictionariesDbPath = File.Exists(dictDbCandidate) ? dictDbCandidate : null,
            IncludeA4Pdf = options.IncludeA4Pdf,
            ExtraPythonPaths = extraPaths,
            PluginConfigs = pluginConfigs,
            Stages = manifestStages
        };

        var tempDir = Path.Combine(dataRootDir, "temp");
        Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, $"pipeline-{book.BookId}-{Guid.NewGuid():N}.json");

        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct).ConfigureAwait(false);

        var logSb = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = extDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Ensure shared plugin site-packages directory is on PYTHONPATH if packages were installed there
        if (Directory.Exists(sharedSitePackagesDir))
        {
            string existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
            string newPythonPath = string.IsNullOrEmpty(existingPythonPath)
                ? sharedSitePackagesDir
                : $"{sharedSitePackagesDir}{Path.PathSeparator}{existingPythonPath}";
            psi.Environment["PYTHONPATH"] = newPythonPath;
        }

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("immersive.cli");
        psi.ArgumentList.Add("run-manifest");
        psi.ArgumentList.Add("--manifest");
        psi.ArgumentList.Add(manifestPath);

        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var success = proc.ExitCode == 0;
            return new StudyPackBuildResult(success, proc.ExitCode, logSb.ToString(), GetBookStudyPack(book.BookId));
        }
        catch (Exception ex)
        {
            return new StudyPackBuildResult(false, -1, $"Failed to launch Python pipeline: {ex.Message}", null);
        }
        finally
        {
            if (File.Exists(manifestPath))
            {
                try { File.Delete(manifestPath); } catch { /* Ignore cleanup error */ }
            }
        }
    }

    public async Task<StudyPackBuildResult> GenerateFromTransferAsync(
        string bookId,
        string transferFilePath,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
    {
        string pythonExe = _configuredPythonExe ?? "";
        if (string.IsNullOrWhiteSpace(pythonExe) || !File.Exists(pythonExe))
        {
            var extRoot = _extractorRoot ?? Path.GetDirectoryName(_outputRoot) ?? "";
            var venvScript = Path.Combine(extRoot, ".venv", "Scripts", "python.exe");
            if (!File.Exists(venvScript))
                venvScript = Path.Combine(extRoot, ".venv", "bin", "python");
            pythonExe = File.Exists(venvScript) ? venvScript : "python";
        }

        var extDir = _extractorRoot ?? Path.GetDirectoryName(_outputRoot) ?? "";
        var logSb = new StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = extDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("immersive.cli");
        psi.ArgumentList.Add("generate-transfer");
        psi.ArgumentList.Add("--transfer");
        psi.ArgumentList.Add(transferFilePath);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(_outputRoot);

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var success = proc.ExitCode == 0;
            return new StudyPackBuildResult(success, proc.ExitCode, logSb.ToString(), GetBookStudyPack(bookId));
        }
        catch (Exception ex)
        {
            return new StudyPackBuildResult(false, -1, $"Failed to run generate-transfer: {ex.Message}", null);
        }
    }

    private static async Task EnsurePipRequirementsAsync(
        string pythonExe,
        string sitePackagesDir,
        IReadOnlyList<string> requirements,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (requirements.Count == 0) return;

        Directory.CreateDirectory(sitePackagesDir);
        var hashInput = string.Join('\n', requirements);
        var hashHex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
        var stampFile = Path.Combine(sitePackagesDir, ".requirements.sha256");

        if (File.Exists(stampFile) && await File.ReadAllTextAsync(stampFile, ct).ConfigureAwait(false) == hashHex)
            return;

        // 1. Check if the requirements are already satisfied in the Python environment
        var missingRequirements = await GetMissingRequirementsAsync(pythonExe, requirements, ct).ConfigureAwait(false);
        if (missingRequirements.Count == 0)
        {
            await File.WriteAllTextAsync(stampFile, hashHex, ct).ConfigureAwait(false);
            return;
        }

        progress?.Report($"Installing missing Python plugin dependencies: {string.Join(", ", missingRequirements)}...");

        // 2. Try 'uv pip install' first if uv is available
        string? uvExe = FindUvExecutable();
        if (!string.IsNullOrEmpty(uvExe))
        {
            var uvPsi = new ProcessStartInfo
            {
                FileName = uvExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            uvPsi.ArgumentList.Add("pip");
            uvPsi.ArgumentList.Add("install");
            uvPsi.ArgumentList.Add("--python");
            uvPsi.ArgumentList.Add(pythonExe);
            uvPsi.ArgumentList.Add("--target");
            uvPsi.ArgumentList.Add(sitePackagesDir);
            foreach (var req in missingRequirements)
                uvPsi.ArgumentList.Add(req);

            using var uvProc = Process.Start(uvPsi);
            if (uvProc is not null)
            {
                await uvProc.WaitForExitAsync(ct).ConfigureAwait(false);
                if (uvProc.ExitCode == 0)
                {
                    await File.WriteAllTextAsync(stampFile, hashHex, ct).ConfigureAwait(false);
                    return;
                }
            }
        }

        // 3. Fallback to 'python -m pip install'
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("pip");
        psi.ArgumentList.Add("install");
        psi.ArgumentList.Add("--target");
        psi.ArgumentList.Add(sitePackagesDir);
        psi.ArgumentList.Add("--upgrade");
        foreach (var req in missingRequirements)
            psi.ArgumentList.Add(req);

        using var proc = Process.Start(psi);
        if (proc is null) return;
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to install plugin Python requirements ({string.Join(", ", missingRequirements)}): {err.Trim()}");
        }

        await File.WriteAllTextAsync(stampFile, hashHex, ct).ConfigureAwait(false);
    }

    private static async Task<List<string>> GetMissingRequirementsAsync(
        string pythonExe,
        IReadOnlyList<string> requirements,
        CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("""
                import sys, re, importlib.util
                missing = []
                for req in sys.argv[1:]:
                    name = re.split(r'[<>=!~]', req)[0].strip().replace('-', '_')
                    if importlib.util.find_spec(name) is None:
                        # Try without normalizing underscores
                        orig = re.split(r'[<>=!~]', req)[0].strip()
                        if importlib.util.find_spec(orig) is None:
                            missing.append(req)
                if missing:
                    print(','.join(missing))
                    sys.exit(1)
                sys.exit(0)
                """);
            foreach (var req in requirements)
                psi.ArgumentList.Add(req);

            using var proc = Process.Start(psi);
            if (proc is null) return requirements.ToList();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            if (proc.ExitCode == 0)
            {
                return [];
            }

            var output = (await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrEmpty(output))
            {
                return output.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            return requirements.ToList();
        }
        catch
        {
            // If check process fails to launch, assume they need installation
            return requirements.ToList();
        }
    }

    private static string? FindUvExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "uv.exe" : "uv");
            if (File.Exists(candidate)) return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidates = new[]
            {
                Path.Combine(userProfile, ".cargo", "bin", "uv.exe"),
                Path.Combine(userProfile, "AppData", "Local", "Programs", "uv", "uv.exe"),
                Path.Combine(userProfile, "AppData", "Roaming", "uv", "uv.exe"),
                Path.Combine(userProfile, "AppData", "Local", "hermes", "bin", "uv.exe")
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
        }

        return null;
    }
}
