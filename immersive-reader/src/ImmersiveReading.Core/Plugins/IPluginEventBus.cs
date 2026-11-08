namespace ImmersiveReading.Core.Plugins;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IPluginEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : class;
    ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class;
    void EmitToUi(string eventName, object? payload);
}
