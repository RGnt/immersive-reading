namespace ImmersiveReading.Core.Plugins;

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Describes a single Python processing stage contributed by a plugin.
/// </summary>
public sealed record PythonStageDescriptor
{
    /// <summary>
    /// Unique identifier for the stage (e.g., "ja.sudachi_morph", "es.verb_paradigms", "ar.root_clustering").
    /// </summary>
    public required string StageId { get; init; }

    /// <summary>
    /// Execution order (1-99: Normalize, 100-199: Tokenize, 200-299: Enrich, 300-399: Filter/Score, 400-499: PDF/Export).
    /// </summary>
    public required int Order { get; init; }

    /// <summary>
    /// ISO 639-1 language codes this stage supports (e.g., ["ja"], ["es"], ["ar"], or ["*"] for all languages).
    /// </summary>
    public IReadOnlyList<string> TargetLanguages { get; init; } = ["*"];

    /// <summary>
    /// Relative path to the Python script file inside the plugin's assets (e.g., "python/ja_stages.py").
    /// </summary>
    public required string ScriptRelativePath { get; init; }

    /// <summary>
    /// Name of the class inside the script that subclasses `immersive.sdk.stage.PipelineStage`.
    /// </summary>
    public required string EntryClassName { get; init; }

    /// <summary>
    /// Additional asset files or directories in the plugin that must be extracted alongside the script.
    /// </summary>
    public IReadOnlyList<string> BundledAssetRelativePaths { get; init; } = [];

    /// <summary>
    /// Optional pip package specifiers required by this stage (e.g., ["sudachipy>=0.6.8", "sudachidict_core"]).
    /// </summary>
    public IReadOnlyList<string> PipRequirements { get; init; } = [];
}

/// <summary>
/// Resolved stage entry written into the temporary pipeline-manifest.json for Python execution.
/// </summary>
public sealed record ResolvedPythonStageManifestEntry
{
    [JsonPropertyName("pluginId")]
    public required string PluginId { get; init; }

    [JsonPropertyName("stageId")]
    public required string StageId { get; init; }

    [JsonPropertyName("order")]
    public required int Order { get; init; }

    [JsonPropertyName("scriptFilePath")]
    public required string ScriptFilePath { get; init; }

    [JsonPropertyName("entryClassName")]
    public required string EntryClassName { get; init; }

    [JsonPropertyName("pluginDataDir")]
    public required string PluginDataDir { get; init; }
}

/// <summary>
/// Top-level JSON contract passed to `python -m immersive.cli run-manifest <path>`.
/// </summary>
public sealed record PythonPipelineRunManifest
{
    [JsonPropertyName("bookId")]
    public required string BookId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("language")]
    public required string Language { get; init; }

    [JsonPropertyName("epubFilePath")]
    public required string EpubFilePath { get; init; }

    [JsonPropertyName("outputDirectory")]
    public required string OutputDirectory { get; init; }

    [JsonPropertyName("deckCsvPath")]
    public string? DeckCsvPath { get; init; }

    [JsonPropertyName("includeA4Pdf")]
    public bool IncludeA4Pdf { get; init; } = true;

    [JsonPropertyName("extraPythonPaths")]
    public IReadOnlyList<string> ExtraPythonPaths { get; init; } = [];

    [JsonPropertyName("pluginConfigs")]
    public Dictionary<string, JsonElement> PluginConfigs { get; init; } = new();

    [JsonPropertyName("stages")]
    public IReadOnlyList<ResolvedPythonStageManifestEntry> Stages { get; init; } = [];
}

