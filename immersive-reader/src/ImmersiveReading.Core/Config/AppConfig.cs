namespace ImmersiveReading.Core.Config;

public sealed record AppConfig(
    DirectorySettings Directories,
    AppearanceSettings Appearance,
    ReaderSettings Reader,
    LlmSettings Llm,
    DictionarySettings Dictionaries,
    LibrarySettings? Library = null
)
{
    public LibrarySettings Library { get; init; } = Library ?? new LibrarySettings();

    public static AppConfig CreateDefault() => new(
        Directories: new DirectorySettings(),
        Appearance: new AppearanceSettings(),
        Reader: new ReaderSettings(),
        Llm: new LlmSettings(),
        Dictionaries: new DictionarySettings(),
        Library: new LibrarySettings()
    );
}

public sealed record DirectorySettings(
    string DefaultScanDirectory = "",
    string ExtractorRootDirectory = "",
    string PythonExecutablePath = "",
    string FlashcardsDirectory = ""
);

public sealed record AppearanceSettings(
    string ThemePreset = "slate",
    string BgPrimary = "#0f172a",
    string BgSurface = "#1e293b",
    string TextPrimary = "#f8fafc",
    string TextMuted = "#94a3b8",
    string BorderColor = "#334155",
    string AccentColor = "#38bdf8",
    string RubyColor = "#e2e8f0",
    string UiFontFamily = "system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif"
);

public sealed record ReaderSettings(
    string FontFamily = "Noto Serif JP, 'Yu Mincho', serif",
    string WritingMode = "vertical-rl",
    int FontSizePx = 22,
    double LineHeight = 1.85,
    string FuriganaMode = "AllKanji",
    bool AutoSavePosition = true,
    int ColumnMaxWidthPx = 900,
    int MaxCacheSizeMb = 500
);

public sealed record LlmSettings(
    string Preset = "ollama",
    string EndpointUrl = "http://localhost:11434/v1/chat/completions",
    string ModelId = "llama3.2",
    string ApiKey = "",
    int TimeoutSeconds = 15,
    double Temperature = 0.7,
    int QuizThresholdPercent = 80,
    int PersonaUnknownThreshold = 1,
    bool EnableOfflineHeuristicFallback = true
);

public sealed record DictionarySettings(
    int MaxScanLength = 16,
    int MaxDeinflections = 4,
    int MaxResults = 12,
    string CardExportFormat = "both",
    bool AutoClipboard = true,
    string AnkiConnectUrl = "http://127.0.0.1:8765",
    string AnkiDeckName = "Japanese::ImmersiveReading",
    string AnkiModelName = "Japanese (Recognition)",
    bool AutoSyncAnki = false
);

public sealed record LibrarySettings(
    string PrimaryGrouping = "series",
    string SortWithinGrouping = "series_and_volume",
    bool EnableShelves = true,
    IReadOnlyList<string>? ExcludedFolderIds = null,
    IReadOnlyList<string>? ExcludedTagIds = null,
    bool HideCompleted = false,
    string DefaultViewMode = "grid"
);
