using System.Text.Json;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Library;

namespace ImmersiveReading.Core.Tests;

public sealed class GlobalConfigurationSpecsTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly LibraryDatabase _database;

    public GlobalConfigurationSpecsTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"imm_test_config_{Guid.NewGuid():N}.db");
        _database = new LibraryDatabase(_tempDbPath);
        _database.InitializeSchema();
    }

    public void Dispose()
    {
        _database.Dispose();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void SeedDefaults_OnFreshDatabase_PersistsAllFiveCategories()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database, "C:\\DefaultBooks");
        var config = configManager.GetConfig();

        Assert.NotNull(config);
        Assert.NotNull(config.Directories);
        Assert.NotNull(config.Appearance);
        Assert.NotNull(config.Reader);
        Assert.NotNull(config.Llm);
        Assert.NotNull(config.Dictionaries);

        Assert.Equal("C:\\DefaultBooks", config.Directories.DefaultScanDirectory);
        Assert.Equal("slate", config.Appearance.ThemePreset);
        Assert.Equal("vertical-rl", config.Reader.WritingMode);
        Assert.Equal("AllKanji", config.Reader.FuriganaMode);
        Assert.Equal("ollama", config.Llm.Preset);
        Assert.Equal(16, config.Dictionaries.MaxScanLength);
    }

    [Fact]
    public async Task UpdateCategory_ValidJson_UpdatesCategoryAndFiresEvent()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        bool eventFired = false;
        AppConfig? notifiedConfig = null;

        configManager.ConfigChanged += (sender, updatedCfg) =>
        {
            eventFired = true;
            notifiedConfig = updatedCfg;
        };

        var newAppearance = new AppearanceSettings(
            ThemePreset: "tokyo-night",
            BgPrimary: "#1a1b26",
            BgSurface: "#24283b",
            TextPrimary: "#c0caf5",
            TextMuted: "#7aa2f7",
            BorderColor: "#414868",
            AccentColor: "#bb9af7",
            RubyColor: "#f7768e",
            UiFontFamily: "Consolas, monospace"
        );

        string json = JsonSerializer.Serialize(newAppearance);
        await configManager.UpdateCategoryJsonAsync("appearance", json);

        var updated = configManager.GetConfig();
        Assert.True(eventFired);
        Assert.NotNull(notifiedConfig);
        Assert.Equal("tokyo-night", updated.Appearance.ThemePreset);
        Assert.Equal("#1a1b26", updated.Appearance.BgPrimary);
        Assert.Equal("Consolas, monospace", updated.Appearance.UiFontFamily);
    }

    [Fact]
    public async Task UpdateEntireConfig_PersistsAllCategoriesAtomically()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        var current = configManager.GetConfig();

        var updatedConfig = current with
        {
            Directories = current.Directories with { DefaultScanDirectory = "D:\\LightNovels" },
            Reader = current.Reader with { FontSizePx = 28, WritingMode = "horizontal-tb" },
            Llm = current.Llm with { Preset = "lmstudio", EndpointUrl = "http://localhost:1234/v1/chat/completions" },
            Dictionaries = current.Dictionaries with { MaxResults = 25 }
        };

        await configManager.UpdateConfigAsync(updatedConfig);

        // Recreate ConfigurationManager to ensure database persistence survived
        var reloadedManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        var reloaded = reloadedManager.GetConfig();

        Assert.Equal("D:\\LightNovels", reloaded.Directories.DefaultScanDirectory);
        Assert.Equal(28, reloaded.Reader.FontSizePx);
        Assert.Equal("horizontal-tb", reloaded.Reader.WritingMode);
        Assert.Equal("lmstudio", reloaded.Llm.Preset);
        Assert.Equal("http://localhost:1234/v1/chat/completions", reloaded.Llm.EndpointUrl);
        Assert.Equal(25, reloaded.Dictionaries.MaxResults);
    }

    [Fact]
    public async Task ResetCategory_ResetsSpecifiedCategoryToDefault()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);

        var modifiedLlm = new LlmSettings(
            Preset: "custom",
            EndpointUrl: "https://custom.endpoint.internal",
            ModelId: "custom-model-v2",
            Temperature: 0.2,
            QuizThresholdPercent: 95
        );
        await configManager.UpdateCategoryJsonAsync("llm", JsonSerializer.Serialize(modifiedLlm));
        Assert.Equal("custom", configManager.GetConfig().Llm.Preset);

        // Reset only LLM category
        await configManager.ResetDefaultsAsync("llm");
        var afterReset = configManager.GetConfig();

        Assert.Equal("ollama", afterReset.Llm.Preset);
        Assert.Equal("http://localhost:11434/v1/chat/completions", afterReset.Llm.EndpointUrl);
        Assert.Equal("llama3.2", afterReset.Llm.ModelId);
    }

    [Fact]
    public async Task ResetAll_ResetsAllCategoriesToDefault()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        var config = configManager.GetConfig();

        var modified = config with
        {
            Directories = config.Directories with { DefaultScanDirectory = "Z:\\Test" },
            Appearance = config.Appearance with { ThemePreset = "warm-paper" },
            Reader = config.Reader with { FontSizePx = 36 }
        };
        await configManager.UpdateConfigAsync(modified);

        await configManager.ResetDefaultsAsync(null);
        var afterReset = configManager.GetConfig();

        Assert.Equal("slate", afterReset.Appearance.ThemePreset);
        Assert.Equal(22, afterReset.Reader.FontSizePx);
    }

    [Fact]
    public async Task UpdateCategory_InvalidCategory_ThrowsArgumentException()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            configManager.UpdateCategoryJsonAsync("non_existent_category", "{}"));
    }

    [Fact]
    public async Task UpdateCategory_InvalidJson_ThrowsArgumentException()
    {
        var configManager = new ImmersiveReading.Library.ConfigurationManager(_database);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            configManager.UpdateCategoryJsonAsync("reader", "this is not valid json"));
    }
}
