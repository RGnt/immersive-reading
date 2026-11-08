namespace ImmersiveReading.Core.Epub;

public static class EpubLanguage
{
    public static string NormalizeLanguageTag(string rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage)) return "ja";
        string primary = rawLanguage.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary switch
        {
            "jpn" => "ja",
            "eng" => "en",
            "zho" or "chi" => "zh",
            "kor" => "ko",
            _ => primary
        };
    }
}

