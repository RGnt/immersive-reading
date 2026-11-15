using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Plugins.JitenMoe;

public sealed class JitenMoeModule : IPluginModule
{
    public string ModuleId => JitenMoePlugin.PluginIdValue;

    public void ConfigureServices(IServiceCollection services) => services.AddSingleton<JitenMoePlugin>();

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
        => [provider.GetRequiredService<JitenMoePlugin>()];

    public void Dispose()
    {
    }
}
