using System.Text.Json;
using ImmersiveReading.Core.Config;
using ImmersiveReading.Plugins.AiCompanion.Providers;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class ConfigRpcModule(IConfigurationManager? configManager = null, IRpcEventSink? events = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("config.getAll", (req, _) => Task.FromResult<object?>(GetAll()));
            yield return ("config.getCategory", (req, _) => Task.FromResult<object?>(GetCategory(req)));
            yield return ("config.update", UpdateConfigAsync);
            yield return ("config.saveAll", UpdateConfigAsync);
            yield return ("config.resetDefaults", ResetDefaultsAsync);
            yield return ("config.testLlmEndpoint", TestLlmEndpointAsync);
        }
    }

    private object? GetAll()
    {
        return configManager?.GetConfig() ?? AppConfig.CreateDefault();
    }

    private object? GetCategory(RpcRequest req)
    {
        string cat = req.RequiredString("category").ToLowerInvariant();
        var cfg = configManager?.GetConfig() ?? AppConfig.CreateDefault();
        return cat switch
        {
            "directories" => (object)cfg.Directories,
            "appearance" => cfg.Appearance,
            "reader" => cfg.Reader,
            "llm" => cfg.Llm,
            "dictionaries" => cfg.Dictionaries,
            "library" => cfg.Library,
            _ => throw new ArgumentException($"Unknown category '{cat}'.")
        };
    }

    private async Task<object?> UpdateConfigAsync(RpcRequest req, CancellationToken ct)
    {
        if (configManager is null) throw new InvalidOperationException("Configuration manager not configured.");

        // Mode 1: Entire configuration tree passed as { config: { directories, appearance, ... } }
        if (req.Params.TryGetProperty("config", out var configProp))
        {
            string fullConfigJson = configProp.GetRawText();
            var newConfig = JsonSerializer.Deserialize<AppConfig>(fullConfigJson, RpcJson.Options)
                ?? throw new ArgumentException("Failed to deserialize configuration object.");

            var updated = await configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = "all", config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        // Mode 2: Per-category update passed as { category: "reader", settings: { ... } }
        string? cat = req.OptionalString("category");
        if (!string.IsNullOrWhiteSpace(cat))
        {
            string settingsJson;
            if (req.Params.TryGetProperty("settings", out var sProp))
            {
                settingsJson = sProp.ValueKind == JsonValueKind.String ? sProp.GetString()! : sProp.GetRawText();
            }
            else
            {
                settingsJson = req.Params.GetRawText();
            }

            var updated = await configManager.UpdateCategoryJsonAsync(cat, settingsJson, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = cat, config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        // Mode 3: The parameters object itself is the AppConfig tree
        if (req.Params.TryGetProperty("reader", out _) || req.Params.TryGetProperty("appearance", out _))
        {
            var newConfig = JsonSerializer.Deserialize<AppConfig>(req.Params.GetRawText(), RpcJson.Options)
                ?? throw new ArgumentException("Failed to deserialize configuration object.");

            var updated = await configManager.UpdateConfigAsync(newConfig, ct).ConfigureAwait(false);
            events?.Emit("config.changed", new { category = "all", config = updated });
            return new { ok = true, updatedConfig = updated };
        }

        throw new ArgumentException("Either 'config' or 'category' must be provided to 'config.update'.");
    }

    private async Task<object?> ResetDefaultsAsync(RpcRequest req, CancellationToken ct)
    {
        if (configManager is null) throw new InvalidOperationException("Configuration manager not configured.");
        string? cat = req.OptionalString("category");
        if (string.Equals(cat, "all", StringComparison.OrdinalIgnoreCase))
        {
            cat = null;
        }
        var updated = await configManager.ResetDefaultsAsync(cat, ct).ConfigureAwait(false);
        events?.Emit("config.changed", new { category = cat ?? "all", config = updated });
        return new { ok = true, updatedConfig = updated };
    }

    private static async Task<object?> TestLlmEndpointAsync(RpcRequest req, CancellationToken ct)
    {
        string endpoint = req.OptionalString("endpointUrl") ?? "http://localhost:11434/v1/chat/completions";
        string apiKey = req.OptionalString("apiKey") ?? "";
        string modelId = req.OptionalString("modelId") ?? "llama3.2";
        int timeout = req.OptionalInt("timeoutSeconds") ?? 5;

        return await LlmEndpointTester.TestAsync(endpoint, apiKey, modelId, timeout, ct).ConfigureAwait(false);
    }
}

