namespace ImmersiveReading.Core.Llm;

/// <summary>
/// Turns the address a person types for an OpenAI-compatible server into the URLs the app calls. Servers are documented in three styles and all
/// three are accepted: the bare host (<c>http://localhost:8091</c>), the base with its version (<c>http://localhost:8091/v1</c>) and the full
/// chat URL (<c>http://localhost:8091/v1/chat/completions</c>). Every client of the app (companion, deck builder, connection tests) must use
/// this one rule: each of them used to read the typed address differently, and a base URL answered 404 while the bare host answered 415.
/// </summary>
public static class LlmEndpoint
{
    private const string ChatSuffix = "/chat/completions";

    public static bool IsOffline(string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint) || endpoint.Trim().StartsWith("offline://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The URL chat requests are POSTed to.</summary>
    public static string ChatCompletionsUrl(string endpoint)
    {
        var (authority, path) = Split(endpoint);
        if (authority is null) return endpoint.Trim();
        return authority + (path.Length == 0 ? "/v1" : path) + ChatSuffix;
    }

    /// <summary>The URLs that list the models, best first. A bare host is tried with <c>/v1</c> first (the standard), then without.</summary>
    public static IReadOnlyList<string> ModelsUrls(string endpoint)
    {
        var (authority, path) = Split(endpoint);
        if (authority is null) return [endpoint.Trim()];
        var urls = new List<string>();
        if (path.Length == 0)
        {
            urls.Add(authority + "/v1/models");
            urls.Add(authority + "/models");
        }
        else
        {
            urls.Add(authority + path + "/models");
        }
        if (authority.EndsWith(":11434", StringComparison.Ordinal)) urls.Add(authority + "/api/tags");      // Ollama's own listing
        return urls;
    }

    /// <summary>(scheme + host + port, the path without a trailing slash and without <c>/chat/completions</c>); a null authority when it is not an http(s) address.</summary>
    private static (string? Authority, string Path) Split(string endpoint)
    {
        if (!Uri.TryCreate((endpoint ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, "");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(ChatSuffix, StringComparison.OrdinalIgnoreCase)) path = path[..^ChatSuffix.Length].TrimEnd('/');
        return (uri.GetLeftPart(UriPartial.Authority), path);
    }
}
