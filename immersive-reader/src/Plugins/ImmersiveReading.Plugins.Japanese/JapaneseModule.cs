using System;
using System.Collections.Generic;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

/// <summary>
/// Plugin module that contributes the Japanese language plugin (MeCab tokenizer, deinflector, ruby aligner).
/// </summary>
public sealed class JapaneseModule : IPluginModule
{
    private JapaneseLanguagePlugin? _plugin;

    public string ModuleId => "japanese";

    public void RegisterLanguages(LanguagePluginRegistry languages)
    {
        _plugin ??= new JapaneseLanguagePlugin();
        languages.Register(_plugin);
    }

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
    {
        _plugin ??= new JapaneseLanguagePlugin();
        return [_plugin];
    }

    public void Dispose()
    {
        _plugin?.Dispose();
        _plugin = null;
    }
}

