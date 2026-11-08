using System.Text.Json;
using ImmersiveReading.Core.Extensions;
using Xunit;

namespace ImmersiveReading.Core.Tests.Extensions;

public sealed class FeaturePluginHostTests
{
    private sealed class StubFeaturePlugin(string id, string actionName) : IFeaturePlugin
    {
        public string PluginId => id;
        public string DisplayName => $"Test Plugin {id}";
        public string Version => "1.0.0";
        public bool IsDisposed { get; private set; }

        public ValueTask InitializeAsync(IFeaturePluginContext context, CancellationToken ct = default)
        {
            context.RegisterRpcAction(actionName, (param, token) => Task.FromResult<object?>("ok"));
            context.RegisterUiPanel(new ReaderUiPanelDescriptor(
                PanelId: $"{id}-panel",
                Title: "Panel",
                Icon: "icon",
                Location: "reader-drawer"));
            return ValueTask.CompletedTask;
        }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task RegisterPluginAsync_PrefixesRpcActionAndRegistersPanel()
    {
        using var host = new FeaturePluginHost(null!, null!, null!);
        var plugin = new StubFeaturePlugin("myPlugin", "doWork");

        await host.RegisterPluginAsync(plugin);

        Assert.Single(host.Plugins);
        Assert.Single(host.UiPanels);
        Assert.Equal("myPlugin-panel", host.UiPanels[0].PanelId);

        // Case-insensitive lookup with plugin prefix
        Assert.True(host.TryGetRpcHandler("ext.myPlugin.doWork", out var handler));
        Assert.NotNull(handler);

        Assert.True(host.TryGetRpcHandler("EXT.MYPLUGIN.DOWORK", out var handlerUpper));
        Assert.NotNull(handlerUpper);

        // Unknown returns false
        Assert.False(host.TryGetRpcHandler("ext.myPlugin.nonExistent", out _));
    }

    [Fact]
    public async Task RegisterPluginAsync_PreservesAlreadyPrefixedActionName()
    {
        using var host = new FeaturePluginHost(null!, null!, null!);
        var plugin = new StubFeaturePlugin("myPlugin", "ext.custom.doWork");

        await host.RegisterPluginAsync(plugin);

        Assert.True(host.TryGetRpcHandler("ext.custom.doWork", out _));
    }

    [Fact]
    public async Task RegisterPluginAsync_ThrowsOnDuplicateAction()
    {
        using var host = new FeaturePluginHost(null!, null!, null!);
        var plugin1 = new StubFeaturePlugin("p1", "actionA");
        var plugin2 = new StubFeaturePlugin("p1", "actionA");

        await host.RegisterPluginAsync(plugin1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.RegisterPluginAsync(plugin2).AsTask());
    }

    [Fact]
    public async Task Dispose_DisposesAllPlugins()
    {
        var host = new FeaturePluginHost(null!, null!, null!);
        var plugin = new StubFeaturePlugin("p1", "action1");
        await host.RegisterPluginAsync(plugin);

        host.Dispose();

        Assert.True(plugin.IsDisposed);
        Assert.Empty(host.Plugins);
        Assert.Empty(host.UiPanels);
        Assert.False(host.TryGetRpcHandler("ext.p1.action1", out _));
    }
}
