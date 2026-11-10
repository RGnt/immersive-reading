using System.Diagnostics;
using System.Text;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Library.StudyPack;

public interface IStudyPackBuilder
{
    Task<StudyPackBuildResult> BuildStudyPackAsync(
        string bookId,
        string bookFolderPath,
        bool readerOnly = false,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default);
}

public sealed class PythonBuildRunner : IStudyPackBuilder
{
    private readonly string _outputRoot;
    private readonly string? _extractorRoot;
    private readonly string? _configuredPythonExe;

    public PythonBuildRunner(string outputRoot, string? extractorRoot = null, string? configuredPythonExe = null)
    {
        _outputRoot = Path.GetFullPath(outputRoot);
        _extractorRoot = extractorRoot is not null
            ? Path.GetFullPath(extractorRoot)
            : Path.GetDirectoryName(_outputRoot);
        _configuredPythonExe = configuredPythonExe;
    }

    public async Task<StudyPackBuildResult> BuildStudyPackAsync(
        string bookId,
        string bookFolderPath,
        bool readerOnly = false,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_extractorRoot) || !Directory.Exists(_extractorRoot))
        {
            return new StudyPackBuildResult(false, -1, "enrichement-extractor root directory not found.", null);
        }

        string pythonExe = ImmersiveReading.Core.Infrastructure.PythonEnvironmentResolver.Instance.ResolvePythonExe(_configuredPythonExe, _extractorRoot) ?? "python";

        string subCommand = readerOnly ? "enrich" : "build";
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = _extractorRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("immersive.cli");
        psi.ArgumentList.Add(subCommand);
        psi.ArgumentList.Add("--book");
        psi.ArgumentList.Add(bookFolderPath);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(_outputRoot);

        var logSb = new StringBuilder();
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

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        bool ok = proc.ExitCode == 0;
        var overview = StudyPackOverviewBuilder.GetBookStudyPack(_outputRoot, bookId);
        return new StudyPackBuildResult(ok, proc.ExitCode, logSb.ToString(), overview);
    }
}

