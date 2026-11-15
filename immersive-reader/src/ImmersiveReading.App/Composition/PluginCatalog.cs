using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Plugins.AiCompanion;
using ImmersiveReading.Plugins.CharacterKnowledge;
using ImmersiveReading.Plugins.DeckBuilder;
using ImmersiveReading.Plugins.Japanese;
using ImmersiveReading.Plugins.JitenMoe;
using ImmersiveReading.Plugins.PosHighlighter;

namespace ImmersiveReading.App.Composition;

/// <summary>
/// Composition catalog declaring the modules loaded by the application.
/// This is the only place in the App project referencing concrete plugin module types.
/// </summary>
public static class PluginCatalog
{
    public static IReadOnlyList<IPluginModule> CreateModules() =>
    [
        new JapaneseModule(),
        new AiCompanionModule(),
        new PosHighlighterModule(),
        new JitenMoeModule(),
        new DeckBuilderModule(),
        new CharacterKnowledgeModule()
    ];
}

