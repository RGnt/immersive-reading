using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Plugins.AiCompanion;
using ImmersiveReading.Plugins.Japanese;

namespace ImmersiveReading.App.Composition;

/// <summary>
/// Composition catalog declaring the modules loaded by the application.
/// This is the only place in the App project referencing concrete plugin module types.
/// </summary>
internal static class PluginCatalog
{
    public static IReadOnlyList<IPluginModule> CreateModules() =>
    [
        new JapaneseModule(),
        new AiCompanionModule()
    ];
}

