namespace ImmersiveReading.Library.Jiten;

public interface IJitenApiKeyProvider
{
    string? GetApiKey();
    void SetApiKey(string? apiKey);
}

public sealed class EnvJitenApiKeyProvider : IJitenApiKeyProvider
{
    public static readonly EnvJitenApiKeyProvider Instance = new();

    private string? _cachedApiKey;
    private bool _apiKeyResolved;

    public string? GetApiKey()
    {
        if (_apiKeyResolved) return _cachedApiKey;

        // 1. Check environment variable
        string? envKey = Environment.GetEnvironmentVariable("JITEN_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            _cachedApiKey = envKey.Trim();
            _apiKeyResolved = true;
            return _cachedApiKey;
        }

        // 2. Search for .env file in parent directories
        try
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string envFile = Path.Combine(dir.FullName, ".env");
                if (File.Exists(envFile))
                {
                    foreach (string line in File.ReadAllLines(envFile))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
                        var parts = trimmed.Split('=', 2);
                        if (parts[0].Trim().Equals("JITEN_API_KEY", StringComparison.OrdinalIgnoreCase))
                        {
                            string val = parts[1].Trim().Trim('"', '\'');
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                _cachedApiKey = val;
                                _apiKeyResolved = true;
                                return _cachedApiKey;
                            }
                        }
                    }
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // Best effort
        }

        _apiKeyResolved = true;
        return _cachedApiKey;
    }

    public void SetApiKey(string? apiKey)
    {
        _cachedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _apiKeyResolved = true;
    }
}

