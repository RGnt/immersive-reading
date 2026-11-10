using System;
using System.Collections.Generic;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosHighlighterModule : IPluginModule
{
    public string ModuleId => PosHighlighterPlugin.PluginIdValue;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<PosHighlighterPlugin>();
    }

    public IReadOnlyList<IImmersivePlugin> CreateImmersivePlugins(IServiceProvider provider)
    {
        return [provider.GetRequiredService<PosHighlighterPlugin>()];
    }

    public void Dispose()
    {
    }
}

