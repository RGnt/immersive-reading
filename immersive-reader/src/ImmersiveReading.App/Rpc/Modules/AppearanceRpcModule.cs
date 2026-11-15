using ImmersiveReading.App.Appearance;

namespace ImmersiveReading.App.Rpc.Modules;

/// <summary>Reads and writes the user's stylesheet. Applying it to the app and the reader is the client's job.</summary>
public sealed class AppearanceRpcModule(UserCssStore store) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("appearance.getUserCss", (req, _) => Task.FromResult<object?>(Dto(store.Get())));
            yield return ("appearance.setUserCss", (req, _) =>
            {
                string css = req.Params.TryGetProperty("css", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String ? c.GetString()! : throw new ArgumentException("css is required.");
                return Task.FromResult<object?>(Dto(store.Set(css, req.OptionalBool("enabled") ?? true)));
            });
            yield return ("appearance.setUserCssEnabled", (req, _) =>
                Task.FromResult<object?>(Dto(store.SetEnabled(req.OptionalBool("enabled") ?? true))));
        }
    }

    private static object Dto(UserCssStore.State s) => new { css = s.Css, enabled = s.Enabled, maxBytes = UserCssStore.MaxBytes };
}
