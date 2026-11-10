namespace ImmersiveReading.App.Plugins;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.Core.Plugins;

public sealed class PluginConfigContext : IPluginConfigContext
{
    private readonly string _pluginId;
    private readonly string _userConfigFilePath;
    private readonly Func<string, Stream?> _assetResolver;
    private readonly Action<string, JsonElement> _onBroadcastConfigChanged;
    private readonly object _sync = new();
    private readonly List<Action<JsonElement>> _subscribers = [];

    private JsonObject _defaultsJson = new();
    private JsonObject _mergedJson = new();
    private JsonElement _cachedSnapshot;
    private JsonElement? _schemaJson;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PluginConfigContext(
        string pluginId,
        string configDirectory,
        Func<string, Stream?> assetResolver,
        Action<string, JsonElement> onBroadcastConfigChanged)
    {
        _pluginId = pluginId;
        Directory.CreateDirectory(configDirectory);
        _userConfigFilePath = Path.Combine(configDirectory, $"{pluginId}.json");
        _assetResolver = assetResolver;
        _onBroadcastConfigChanged = onBroadcastConfigChanged;

        LoadInitial();
    }

    private void LoadInitial()
    {
        using (var defaultsStream = _assetResolver("settings.defaults.json"))
        {
            if (defaultsStream is not null)
                _defaultsJson = JsonNode.Parse(defaultsStream)?.AsObject() ?? new JsonObject();
        }

        using (var schemaStream = _assetResolver("settings.schema.json"))
        {
            if (schemaStream is not null)
            {
                using var doc = JsonDocument.Parse(schemaStream);
                _schemaJson = doc.RootElement.Clone();
            }
        }

        var userOverrides = new JsonObject();
        if (File.Exists(_userConfigFilePath))
        {
            try
            {
                var raw = File.ReadAllText(_userConfigFilePath);
                userOverrides = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject();
            }
            catch { /* Fallback to defaults */ }
        }

        _mergedJson = MergeJsonObjects(_defaultsJson, userOverrides);
        using var snapshotDoc = JsonDocument.Parse(_mergedJson.ToJsonString());
        _cachedSnapshot = snapshotDoc.RootElement.Clone();
    }

    public T Get<T>() where T : class, new()
    {
        lock (_sync)
        {
            return _mergedJson.Deserialize<T>(SerializerOptions) ?? new T();
        }
    }

    public JsonElement GetRawJson()
    {
        lock (_sync)
        {
            return _cachedSnapshot.Clone();
        }
    }

    public JsonElement? GetSchemaJson() => _schemaJson;

    public async ValueTask UpdateAsync(JsonElement partialOrFullConfig, CancellationToken ct = default)
    {
        JsonElement snapshot;
        List<Action<JsonElement>> listeners;

        lock (_sync)
        {
            var incomingObj = JsonNode.Parse(partialOrFullConfig.GetRawText())?.AsObject() ?? new JsonObject();
            _mergedJson = MergeJsonObjects(_mergedJson, incomingObj);
            using var snapshotDoc = JsonDocument.Parse(_mergedJson.ToJsonString());
            _cachedSnapshot = snapshotDoc.RootElement.Clone();
            snapshot = _cachedSnapshot.Clone();
            listeners = [.. _subscribers];
        }

        await File.WriteAllTextAsync(_userConfigFilePath, snapshot.GetRawText(), ct);

        foreach (var listener in listeners)
            listener(snapshot);

        _onBroadcastConfigChanged(_pluginId, snapshot);
    }

    public ValueTask ResetToDefaultsAsync(CancellationToken ct = default)
    {
        JsonElement snapshot;
        List<Action<JsonElement>> listeners;

        lock (_sync)
        {
            _mergedJson = (JsonObject)_defaultsJson.DeepClone();
            using var snapshotDoc = JsonDocument.Parse(_mergedJson.ToJsonString());
            _cachedSnapshot = snapshotDoc.RootElement.Clone();
            snapshot = _cachedSnapshot.Clone();
            listeners = [.. _subscribers];
        }

        if (File.Exists(_userConfigFilePath))
            File.Delete(_userConfigFilePath);

        foreach (var listener in listeners)
            listener(snapshot);

        _onBroadcastConfigChanged(_pluginId, snapshot);
        return ValueTask.CompletedTask;
    }

    public IDisposable OnConfigChanged<T>(Action<T> onChanged) where T : class, new()
    {
        Action<JsonElement> wrapper = elem =>
        {
            var typed = elem.Deserialize<T>(SerializerOptions) ?? new T();
            onChanged(typed);
        };

        lock (_sync) { _subscribers.Add(wrapper); }
        return new CallbackDisposable(() =>
        {
            lock (_sync) { _subscribers.Remove(wrapper); }
        });
    }

    private static JsonObject MergeJsonObjects(JsonObject baseObj, JsonObject overrides)
    {
        var result = (JsonObject)baseObj.DeepClone();
        foreach (var (key, value) in overrides)
        {
            if (value is JsonObject overrideChild && result[key] is JsonObject baseChild)
                result[key] = MergeJsonObjects(baseChild, overrideChild);
            else
                result[key] = value?.DeepClone();
        }
        return result;
    }
}
