namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <param name="Problem">When the builder cannot be started: what is missing and the exact command that fixes it.</param>
public sealed record BuilderLocation(string? Directory, string? PythonExe, string? Problem)
{
    public bool Found => Problem is null;
}

/// <summary>
/// Finds the <c>knowledge-graph</c> project (the Python builder) and its interpreter. The builder is a separate project with its
/// own virtual environment (it calls an LLM, unlike the offline study pipeline), so it is never assumed to exist.
/// </summary>
public static class BuilderLocator
{
    public const string FolderName = "knowledge-graph";

    public static BuilderLocation Locate(CharacterKnowledgeSettings settings, string? startDirectory = null)
    {
        string? directory = !string.IsNullOrWhiteSpace(settings.BuilderDirectory)
            ? settings.BuilderDirectory.Trim()
            : FindUpwards(startDirectory ?? AppContext.BaseDirectory);

        if (directory is null || !Directory.Exists(Path.Combine(directory, "immersive_knowledge")))
        {
            return new BuilderLocation(directory, null,
                $"The '{FolderName}' folder was not found. It sits next to 'enrichement-extractor' in the repository. " +
                "Set its location under Settings → Installed Plugins → Character Knowledge → knowledge-graph folder.");
        }

        string? python = !string.IsNullOrWhiteSpace(settings.PythonPath) && File.Exists(settings.PythonPath.Trim())
            ? settings.PythonPath.Trim()
            : new[] { Path.Combine(directory, ".venv", "Scripts", "python.exe"), Path.Combine(directory, ".venv", "bin", "python") }
                .FirstOrDefault(File.Exists);

        if (python is null)
        {
            return new BuilderLocation(directory, null,
                "The builder has no Python environment yet. Run once in a terminal:  " +
                $"cd \"{directory}\"  then  uv venv --python 3.12 .venv  then  uv pip install --python .venv\\Scripts\\python.exe -e .   " +
                "(or set the Python executable in the plugin settings).");
        }

        return new BuilderLocation(directory, python, null);
    }

    private static string? FindUpwards(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, FolderName);
            if (Directory.Exists(Path.Combine(candidate, "immersive_knowledge"))) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
