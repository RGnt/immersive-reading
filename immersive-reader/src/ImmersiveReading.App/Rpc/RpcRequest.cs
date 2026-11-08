using System.Text.Json;

namespace ImmersiveReading.App.Rpc;

public readonly record struct RpcRequest(string Method, JsonElement Params)
{
    public string RequiredString(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            string? val = el.GetString();
            if (!string.IsNullOrWhiteSpace(val)) return val;
        }
        throw new ArgumentException($"Missing required parameter '{propName}'.");
    }

    public string? OptionalString(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.String)
        {
            return el.GetString();
        }
        return null;
    }

    public int? OptionalInt(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int i)) return i;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int pi)) return pi;
        }
        return null;
    }

    public double? OptionalDouble(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d)) return d;
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pd)) return pd;
        }
        return null;
    }

    public bool? OptionalBool(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool pb)) return pb;
        }
        return null;
    }

    public List<string> StringList(string propName)
    {
        if (Params.ValueKind == JsonValueKind.Object &&
            Params.TryGetProperty(propName, out var el) &&
            el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string? s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            return list;
        }
        return [];
    }

    public T? Bind<T>()
    {
        if (Params.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<T>(Params.GetRawText(), RpcJson.Options);
        }
        return default;
    }
}

