namespace ImmersiveReading.App.Plugins;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmersiveReading.App.Rpc;
using ImmersiveReading.Core.Plugins;

public sealed class PluginEventBusHub(IRpcEventSink rpcEvents)
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subscriptions = new();
    private readonly object _subLock = new();

    public IPluginEventBus CreateScopedBus(string pluginId) => new ScopedBus(this, pluginId, rpcEvents);

    private sealed class ScopedBus(PluginEventBusHub hub, string pluginId, IRpcEventSink rpcEvents) : IPluginEventBus, IDisposable
    {
        private readonly List<IDisposable> _disposables = [];

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : class
        {
            var eventType = typeof(TEvent);
            lock (hub._subLock)
            {
                var list = hub._subscriptions.GetOrAdd(eventType, _ => []);
                list.Add(handler);
            }

            var sub = new CallbackDisposable(() =>
            {
                lock (hub._subLock)
                {
                    if (hub._subscriptions.TryGetValue(eventType, out var list))
                    {
                        list.Remove(handler);
                    }
                }
            });
            _disposables.Add(sub);
            return sub;
        }

        public async ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class
        {
            List<Delegate> handlers;
            lock (hub._subLock)
            {
                if (!hub._subscriptions.TryGetValue(typeof(TEvent), out var list))
                    return;
                handlers = [.. list];
            }

            foreach (var del in handlers)
            {
                if (del is Func<TEvent, CancellationToken, ValueTask> typed)
                {
                    await typed(evt, ct);
                }
            }
        }

        public void EmitToUi(string eventName, object? payload)
        {
            var fullEvent = eventName.StartsWith("ext.", StringComparison.OrdinalIgnoreCase)
                ? eventName
                : $"ext.{pluginId}.{eventName}";
            rpcEvents.Emit(fullEvent, payload ?? new { });
        }

        public void Dispose()
        {
            for (var i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }
            _disposables.Clear();
        }
    }
}
