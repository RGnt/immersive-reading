using ImmersiveReading.Core.Prompts;

namespace ImmersiveReading.App.Rpc.Modules;

/// <summary>Lets the user read, check, replace and reset the prompts of every feature (Settings → Prompts). All rules live in <see cref="IPromptCatalog"/>.</summary>
public sealed class PromptsRpcModule(IPromptCatalog catalog) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("prompts.list", (req, _) => Task.FromResult<object?>(new { prompts = catalog.List().Select(Dto) }));
            yield return ("prompts.check", (req, _) =>
            {
                var check = catalog.Check(req.RequiredString("name"), req.RequiredString("text"));
                return Task.FromResult<object?>(new { ok = check.Ok, error = check.Error, line = check.Line, warnings = check.Warnings });
            });
            yield return ("prompts.save", (req, _) =>
            {
                string name = req.RequiredString("name");
                try
                {
                    catalog.SaveOverride(name, req.RequiredString("text"));
                }
                catch (PromptTemplateException ex)
                {
                    return Task.FromResult<object?>(new { ok = false, error = ex.Message, line = ex.Line, prompt = (object?)null });
                }
                return Task.FromResult<object?>(new { ok = true, error = (string?)null, line = 0, prompt = Dto(catalog.Get(name)!) });
            });
            yield return ("prompts.reset", (req, _) =>
            {
                string name = req.RequiredString("name");
                bool removed = catalog.ResetOverride(name);
                return Task.FromResult<object?>(new { ok = true, removed, prompt = Dto(catalog.Get(name)!) });
            });
        }
    }

    private static object Dto(PromptInfo p) => new
    {
        name = p.Name,
        owner = p.Owner,
        version = p.Version,
        purpose = p.Purpose,
        response = p.Response,
        variables = p.Variables.Select(v => new { name = v.Name, required = v.Required, untrusted = v.Untrusted, type = v.Type, @default = v.Default, @enum = v.Enum }),
        builtInText = p.BuiltInText,
        text = p.OverrideText ?? p.BuiltInText,
        usingOverride = p.UsingOverride,
        overrideError = p.OverrideError
    };
}
