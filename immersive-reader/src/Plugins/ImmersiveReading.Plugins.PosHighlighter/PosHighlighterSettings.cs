using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosHighlighterSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("highlightStyle")]
    public string HighlightStyle { get; set; } = "text-color";

    [JsonPropertyName("colorAdjI")]
    public string ColorAdjI { get; set; } = "#fb923c";

    [JsonPropertyName("colorAdjNa")]
    public string ColorAdjNa { get; set; } = "#f472b6";

    [JsonPropertyName("colorVerbGodan")]
    public string ColorVerbGodan { get; set; } = "#38bdf8";

    [JsonPropertyName("colorVerbIchidan")]
    public string ColorVerbIchidan { get; set; } = "#818cf8";

    [JsonPropertyName("colorVerbIrregular")]
    public string ColorVerbIrregular { get; set; } = "#c084fc";

    [JsonPropertyName("colorSuffix")]
    public string ColorSuffix { get; set; } = "#facc15";

    [JsonPropertyName("colorParticleSingle")]
    public string ColorParticleSingle { get; set; } = "#34d399";

    [JsonPropertyName("colorParticleMulti")]
    public string ColorParticleMulti { get; set; } = "#2dd4bf";

    [JsonPropertyName("colorAdverb")]
    public string ColorAdverb { get; set; } = "#a3e635";

    [JsonPropertyName("colorNoun")]
    public string ColorNoun { get; set; } = "#93c5fd";

    [JsonPropertyName("colorConjunction")]
    public string ColorConjunction { get; set; } = "#fb7185";

    [JsonPropertyName("colorAuxiliary")]
    public string ColorAuxiliary { get; set; } = "#38bdf8";

    [JsonPropertyName("colorInterjection")]
    public string ColorInterjection { get; set; } = "#e879f9";

    [JsonPropertyName("maxCacheSizeMb")]
    public int MaxCacheSizeMb { get; set; } = 500;

    [JsonPropertyName("cacheSizeMb")]
    public int CacheSizeMb
    {
        get => MaxCacheSizeMb;
        set => MaxCacheSizeMb = value;
    }

    [JsonPropertyName("usePythonNlp")]
    public bool UsePythonNlp { get; set; } = true;

    [JsonPropertyName("pythonPath")]
    public string? PythonPath { get; set; } = null;

    public static PosHighlighterSettings FromJson(JsonElement element)
    {
        try
        {
            return JsonSerializer.Deserialize<PosHighlighterSettings>(element.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new PosHighlighterSettings();
        }
        catch
        {
            return new PosHighlighterSettings();
        }
    }
}
