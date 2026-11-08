namespace ImmersiveReading.Core.Config;

public interface IConfigurationManager
{
    AppConfig CurrentConfig { get; }
    AppConfig GetConfig();
    Task<AppConfig> UpdateConfigAsync(AppConfig newConfig, CancellationToken ct = default);
    Task<AppConfig> UpdateCategoryJsonAsync(string category, string categoryJson, CancellationToken ct = default);
    Task<AppConfig> ResetDefaultsAsync(string? category = null, CancellationToken ct = default);
    event EventHandler<AppConfig>? ConfigChanged;
}
