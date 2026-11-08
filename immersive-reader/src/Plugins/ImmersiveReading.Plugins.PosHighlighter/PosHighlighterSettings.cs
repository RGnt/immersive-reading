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
