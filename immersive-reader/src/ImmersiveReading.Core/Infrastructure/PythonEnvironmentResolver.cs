namespace ImmersiveReading.Core.Infrastructure;

using System;
using System.IO;
using ImmersiveReading.Core.Abstractions;

public sealed class PythonEnvironmentResolver : IPythonEnvironmentResolver
{
    public static readonly PythonEnvironmentResolver Instance = new();

    private string? _cachedExtractorDir;
    private string? _cachedPythonExe;

    public string? ResolveExtractorDir(string? startDir = null)
    {
        if (!string.IsNullOrEmpty(_cachedExtractorDir) && Directory.Exists(_cachedExtractorDir))
        {
            return _cachedExtractorDir;
        }

        var dir = new DirectoryInfo(startDir ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "enrichement-extractor");
            if (Directory.Exists(candidate))
            {
                _cachedExtractorDir = candidate;
                return candidate;
            }
            dir = dir.Parent;
        }

        return null;
    }

    public string? ResolvePythonExe(string? configuredPath = null, string? extractorDir = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        if (!string.IsNullOrEmpty(_cachedPythonExe) && File.Exists(_cachedPythonExe))
        {
            return _cachedPythonExe;
        }

        extractorDir ??= ResolveExtractorDir();
        if (!string.IsNullOrEmpty(extractorDir))
        {
            string winVenv = Path.Combine(extractorDir, ".venv", "Scripts", "python.exe");
            if (File.Exists(winVenv))
            {
                _cachedPythonExe = winVenv;
                return winVenv;
            }

            string nixVenv = Path.Combine(extractorDir, ".venv", "bin", "python");
            if (File.Exists(nixVenv))
            {
                _cachedPythonExe = nixVenv;
                return nixVenv;
            }
        }

        return "python";
    }
}

