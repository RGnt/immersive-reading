using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Plugins.AiCompanion.Providers;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class ConfigRpcModule(IConfigurationManager? configManager = null, IRpcEventSink? events = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("config.getAll", (req, _) => Task.FromResult<object?>(GetAll()));
            yield return ("config.getCategory", (req, _) => Task.FromResult<object?>(GetCategory(req)));
            yield return ("config.update", UpdateConfigAsync);
            yield return ("config.saveAll", UpdateConfigAsync);
            yield return ("config.resetDefaults", ResetDefaultsAsync);
            yield return ("config.testLlmEndpoint", TestLlmEndpointAsync);
            yield return ("config.detectPython", (req, _) => Task.FromResult<object?>(DetectPython()));
            yield return ("config.setupPortablePython", SetupPortablePythonAsync);
        }
    }

    private object? GetAll()
    {
        return configManager?.GetConfig() ?? AppConfig.CreateDefault();
    }

    private object? GetCategory(RpcRequest req)
    {
        string cat = req.RequiredString("category").ToLowerInvariant();
        var cfg = configManager?.GetConfig() ?? AppConfig.CreateDefault();
        return cat switch
        {
            "directories" => (object)cfg.Directories,
            "appearance" => cfg.Appearance,
            "reader" => cfg.Reader,
            "llm" => cfg.Llm,
            "dictionaries" => cfg.Dictionaries,
            "library" => cfg.Library,
            _ => throw new ArgumentException($"Unknown category '{cat}'.")
        };
    }

    private async Task<object?> UpdateConfigAsync(RpcRequest req, CancellationToken ct)
    {
        if (configManager is null) throw new InvalidOperationException("Configuration manager not configured.");

        // Mode 1: Entire configuration tree passed as { config: { directories, appearance, ... } }
        if (req.Params.TryGetProperty("config", out var configProp))
        {
            string fullConfigJson = configProp.GetRawText();
            var newConfig = JsonSerializer.Deserialize<AppConfig>(fullConfigJson, RpcJson.Options)
                ?? throw new ArgumentException("Failed to deserialize configuration object.");

            var updated = await configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = "all", config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        // Mode 2: Per-category update passed as { category: "reader", settings: { ... } }
        string? cat = req.OptionalString("category");
        if (!string.IsNullOrWhiteSpace(cat))
        {
            string settingsJson;
            if (req.Params.TryGetProperty("settings", out var sProp))
            {
                settingsJson = sProp.ValueKind == JsonValueKind.String ? sProp.GetString()! : sProp.GetRawText();
            }
            else
            {
                settingsJson = req.Params.GetRawText();
            }

            var updated = await configManager.UpdateCategoryJsonAsync(cat, settingsJson, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = cat, config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        // Mode 3: The parameters object itself is the AppConfig tree
        if (req.Params.TryGetProperty("reader", out _) || req.Params.TryGetProperty("appearance", out _))
        {
            var newConfig = JsonSerializer.Deserialize<AppConfig>(req.Params.GetRawText(), RpcJson.Options)
                ?? throw new ArgumentException("Failed to deserialize configuration object.");

            var updated = await configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = "all", config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        throw new ArgumentException("Either 'config' or 'category' must be provided to 'config.update'.");
    }

    private async Task<object?> ResetDefaultsAsync(RpcRequest req, CancellationToken ct)
    {
        if (configManager is null) throw new InvalidOperationException("Configuration manager not configured.");
        string? cat = req.OptionalString("category");
        if (string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase))
        {
            cat = null;
        }
        var updated = await configManager.ResetDefaultsAsync(cat, ct).ConfigureAwait(false);
        events?.Emit("config.changed", new { category = cat ?? "all", config = updated });
        return new { ok = true, updatedConfig = updated };
    }

    private static async Task<object?> TestLlmEndpointAsync(RpcRequest req, CancellationToken ct)
    {
        string endpoint = req.OptionalString("endpointUrl") ?? "http://localhost:11434/v1/chat/completions";
        string apiKey = req.OptionalString("apiKey") ?? "";
        string modelId = req.OptionalString("modelId") ?? "llama3.2";
        int timeout = req.OptionalInt("timeoutSeconds") ?? 5;

        return await LlmEndpointTester.TestAsync(endpoint, apiKey, modelId, timeout, ct).ConfigureAwait(false);
    }

    private sealed record PythonDetectResult(
        bool Found,
        string? PythonPath,
        string? Version,
        bool IsConfigured,
        bool PortableZipExists,
        string? PortableZipPath
    );

    private PythonDetectResult DetectPython()
    {
        var cfg = configManager?.GetConfig() ?? AppConfig.CreateDefault();
        string configuredPath = cfg.Directories.PythonExecutablePath?.Trim() ?? "";

        // Check configured path first
        if (!string.IsNullOrEmpty(configuredPath) && (File.Exists(configuredPath) || configuredPath == "python3" || configuredPath == "python"))
        {
            var (ok, ver) = CheckPythonVersion(configuredPath);
            if (ok)
            {
                return new PythonDetectResult(
                    Found: true,
                    PythonPath: configuredPath,
                    Version: ver,
                    IsConfigured: true,
                    PortableZipExists: FindPortableZip() != null,
                    PortableZipPath: FindPortableZip()
                );
            }
        }

        // Search candidates
        var candidates = GetCandidatePythonPaths(cfg.Directories.ExtractorRootDirectory);
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                var (ok, ver) = CheckPythonVersion(path);
                if (ok)
                {
                    return new PythonDetectResult(
                        Found: true,
                        PythonPath: path,
                        Version: ver,
                        IsConfigured: false,
                        PortableZipExists: FindPortableZip() != null,
                        PortableZipPath: FindPortableZip()
                    );
                }
            }
        }

        // Try system python3 / python on PATH
        foreach (var sysCmd in new[] { "python3", "python" })
        {
            var (ok, ver) = CheckPythonVersion(sysCmd);
            if (ok)
            {
                return new PythonDetectResult(
                    Found: true,
                    PythonPath: sysCmd,
                    Version: ver,
                    IsConfigured: false,
                    PortableZipExists: FindPortableZip() != null,
                    PortableZipPath: FindPortableZip()
                );
            }
        }

        return new PythonDetectResult(
            Found: false,
            PythonPath: null,
            Version: null,
            IsConfigured: false,
            PortableZipExists: FindPortableZip() != null,
            PortableZipPath: FindPortableZip()
        );
    }

    private async Task<object?> SetupPortablePythonAsync(RpcRequest req, CancellationToken ct)
    {
        if (configManager is null) throw new InvalidOperationException("Configuration manager not configured.");

        string? zipPath = FindPortableZip();
        if (zipPath is not null && File.Exists(zipPath))
        {
            string targetDir = Path.Combine(AppContext.BaseDirectory, ".library-data", "python-runtime");
            Directory.CreateDirectory(targetDir);

            // Extract zip if not already extracted
            string extractedExe = Path.Combine(targetDir, "ImmersivePipeline-windows-x64", "python", "python.exe");
            if (!File.Exists(extractedExe))
            {
                extractedExe = Path.Combine(targetDir, "python", "python.exe");
            }

            if (!File.Exists(extractedExe))
            {
                events?.Emit("python.setupProgress", new { status = "extracting", zipPath, targetDir });
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true), ct).ConfigureAwait(false);

                extractedExe = Directory.EnumerateFiles(targetDir, "python.exe", SearchOption.AllDirectories).FirstOrDefault()
                    ?? Path.Combine(targetDir, "python", "python.exe");
            }

            if (File.Exists(extractedExe))
            {
                var (ok, ver) = CheckPythonVersion(extractedExe);
                var current = configManager.GetConfig();
                var updated = current with
                {
                    Directories = current.Directories with { PythonExecutablePath = extractedExe }
                };
                await configManager.UpdateConfigAsync(updated, ct).ConfigureAwait(false);
                events?.Emit("config.changed", new { category = "directories", config = updated });

                return new
                {
                    ok = true,
                    pythonPath = extractedExe,
                    version = ver,
                    message = "Portable Python environment successfully extracted and configured!"
                };
            }
        }

        // Fallback: auto-detect an existing candidate and configure it
        var detectResult = DetectPython();
        if (detectResult.Found && !string.IsNullOrEmpty(detectResult.PythonPath))
        {
            string detectedPath = detectResult.PythonPath;
            var current = configManager.GetConfig();
            var updated = current with
            {
                Directories = current.Directories with { PythonExecutablePath = detectedPath }
            };
            await configManager.UpdateConfigAsync(updated, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = "directories", config = updated });

            return new
            {
                ok = true,
                pythonPath = detectedPath,
                version = detectResult.Version,
                message = "Configured detected Python environment!"
            };
        }

        throw new FileNotFoundException("Portable Python distribution package not found. Run 'scripts/build_portable.py' or provide a Python executable path.");
    }

    private static (bool Ok, string Version) CheckPythonVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath) && exePath != "python" && exePath != "python3") return (false, "");
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            string stderr = proc.StandardError.ReadToEnd().Trim();
            if (!proc.WaitForExit(3000))
            {
                try { proc.Kill(); } catch { }
                return (false, "Timeout");
            }
            string version = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
            return (proc.ExitCode == 0, version);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string? FindPortableZip()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "enrichement-extractor", "dist", "ImmersivePipeline-windows-x64.zip"),
            Path.Combine(Directory.GetCurrentDirectory(), "enrichement-extractor", "dist", "ImmersivePipeline-windows-x64.zip"),
            Path.Combine(AppContext.BaseDirectory, "dist", "ImmersivePipeline-windows-x64.zip"),
            Path.Combine(AppContext.BaseDirectory, "ImmersivePipeline-windows-x64.zip")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<string> GetCandidatePythonPaths(string? extractorRoot)
    {
        var list = new List<string>();

        if (!string.IsNullOrWhiteSpace(extractorRoot))
        {
            list.Add(Path.Combine(extractorRoot, ".venv", "Scripts", "python.exe"));
            list.Add(Path.Combine(extractorRoot, ".venv", "bin", "python"));
            list.Add(Path.Combine(extractorRoot, "python", "python.exe"));
            list.Add(Path.Combine(extractorRoot, "python", "bin", "python3"));
        }

        list.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "enrichement-extractor", ".venv", "Scripts", "python.exe"));
        list.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "enrichement-extractor", ".venv", "bin", "python"));
        list.Add(Path.Combine(Directory.GetCurrentDirectory(), "enrichement-extractor", ".venv", "Scripts", "python.exe"));
        list.Add(Path.Combine(Directory.GetCurrentDirectory(), "enrichement-extractor", ".venv", "bin", "python"));
        list.Add(Path.Combine(AppContext.BaseDirectory, ".library-data", "python-runtime", "ImmersivePipeline-windows-x64", "python", "python.exe"));
        list.Add(Path.Combine(AppContext.BaseDirectory, ".library-data", "python-runtime", "python", "python.exe"));
        list.Add(Path.Combine(AppContext.BaseDirectory, ".library-data", "python-runtime", "bin", "python3"));
        list.Add(Path.Combine(AppContext.BaseDirectory, "python", "python.exe"));
        list.Add(Path.Combine(AppContext.BaseDirectory, "dist", "ImmersivePipeline-windows-x64", "python", "python.exe"));

        return list;
    }
}

