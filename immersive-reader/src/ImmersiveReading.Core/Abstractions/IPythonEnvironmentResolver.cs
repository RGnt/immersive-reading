namespace ImmersiveReading.Core.Abstractions;

public interface IPythonEnvironmentResolver
{
    string? ResolvePythonExe(string? configuredPath = null, string? extractorDir = null);
    string? ResolveExtractorDir(string? startDir = null);
}

