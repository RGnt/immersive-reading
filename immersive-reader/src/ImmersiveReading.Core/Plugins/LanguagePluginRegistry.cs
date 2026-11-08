namespace ImmersiveReading.Core.Plugins;

public sealed class LanguagePluginRegistry : ILanguagePluginResolver, IDisposable
{
    private readonly Dictionary<string, ILanguagePlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);
    private ILanguagePlugin? _fallbackPlugin;

    public void Register(ILanguagePlugin plugin, bool isFallback = false)
    {
        _plugins[plugin.LanguageCode] = plugin;
        if (isFallback || plugin.LanguageCode == "*")
        {
            _fallbackPlugin = plugin;
        }
    }

    public ILanguagePlugin Resolve(string? languageCode)
    {
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            string primary = languageCode.Trim().Split('-', '_')[0].ToLowerInvariant();
            if (_plugins.TryGetValue(primary, out var match))
            {
                return match;
            }
        }

        return _fallbackPlugin
            ?? _plugins.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No ILanguagePlugin registered in LanguagePluginRegistry.");
    }

    public IReadOnlyList<ILanguagePlugin> ListPlugins() => _plugins.Values.ToList();

    public void Dispose()
    {
        foreach (var p in _plugins.Values)
        {
            p.Dispose();
        }
    }
}

