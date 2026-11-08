using System.Text.Json;

namespace ImmersiveReading.Dictionaries.Lookup;

internal static class PitchFrequencyParser
{
    public static void ParsePitches(string json, List<PitchAccentInfo> output)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("pitches", out var pitchesProp) && pitchesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pitchesProp.EnumerateArray())
                {
                    int pos = p.TryGetProperty("position", out var posProp) ? posProp.GetInt32() : 0;
                    string pattern = pos switch
                    {
                        0 => "Heiban",
                        1 => "Atamadaka",
                        _ => "Nakadaka"
                    };
                    output.Add(new PitchAccentInfo("", pos, pattern));
                }
            }
        }
        catch
        {
        }
    }

    public static void ParseFreq(string dictTitle, string json, List<FrequencyBadge> output)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("frequency", out var freqProp))
            {
                if (freqProp.ValueKind == JsonValueKind.Number)
                {
                    int rank = freqProp.GetInt32();
                    output.Add(new FrequencyBadge(dictTitle, $"#{rank}", rank));
                }
                else if (freqProp.ValueKind == JsonValueKind.String)
                {
                    output.Add(new FrequencyBadge(dictTitle, freqProp.GetString()!, null));
                }
            }
        }
        catch
        {
        }
    }
}

