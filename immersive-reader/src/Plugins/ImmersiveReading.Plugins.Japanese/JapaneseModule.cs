using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

/// <summary>
/// Plugin module that contributes the Japanese language plugin (MeCab tokenizer, deinflector, ruby aligner).
/// </summary>
public sealed class JapaneseModule : IPluginModule
{
    public string ModuleId => "japanese";

    public void RegisterLanguages(LanguagePluginRegistry languages)
    {
        // Ownership of the plugin instance passes to the registry, which disposes it.
        languages.Register(new JapaneseLanguagePlugin());
    }

    public void Dispose()
    {
    }
}

