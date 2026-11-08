namespace ImmersiveReading.Core.Plugins;

[Flags]
public enum LanguageCapabilities
{
    None                    = 0,
    MorphologicalTokenizer  = 1 << 0,
    Deinflection            = 1 << 1,
    RubyPhonetics           = 1 << 2,
    CharacterDecomposition  = 1 << 3,
    PitchOrToneAccent       = 1 << 4,
    VerticalTypography      = 1 << 5
}

public enum RubySource : byte
{
    None = 0,
    EpubAuthor = 1,
    PluginGenerated = 2
}

public enum WordStatus : byte
{
    New = 0,
    Learning = 1,
    Known = 2,
    Ignored = 3
}

public enum FuriganaMode
{
    AllKanji,
    UnknownAndLearningOnly,
    AuthorOnly,
    Hover,
    Off
}

