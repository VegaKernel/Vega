using System.Runtime.CompilerServices;
using Vega.Contracts.Events;
using Vega.Contracts.Modules;

namespace Vega.PluginSdk;

internal static class VegaPluginEventRuntime
{
    private static readonly ConditionalWeakTable<IVegaPluginContext, PluginEvents> Facades = new();
    private static IAsyncEventHub? hub;

    internal static void Configure(IAsyncEventHub value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref hub, value);
    }

    internal static IPluginEvents Events(IVegaPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Facades.GetValue(context, static value => new PluginEvents(value));
    }

    private static IAsyncEventHub Hub => Volatile.Read(ref hub) ??
        throw new InvalidOperationException("Plugin event capability is unavailable in this Vega host.");

    private sealed class PluginEvents(IVegaPluginContext context) : IPluginEvents
    {
        public void On<TEvent>(
            Action<EventContext<TEvent>> handler,
            EventPriority priority = EventPriority.Normal,
            bool invokeWhenCancelled = false)
        {
            ArgumentNullException.ThrowIfNull(handler);
            IHotModuleContext owner = Owner();
            var adapter = new DelegateHandler<TEvent>(handler, priority, invokeWhenCancelled);
            IDisposable registration = Hub.Register(adapter);
            owner.Track(new EventRegistration(registration, adapter.Deactivate));
        }

        public void Observe<TEvent>(
            Action<EventOutcome<TEvent>> monitor,
            EventPriority priority = EventPriority.Normal)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            IHotModuleContext owner = Owner();
            var adapter = new DelegateMonitor<TEvent>(monitor, priority);
            IDisposable registration = Hub.Monitor(adapter);
            owner.Track(new EventRegistration(registration, adapter.Deactivate));
        }

        private IHotModuleContext Owner() => context as IHotModuleContext ??
            throw new InvalidOperationException(
                "Plugin event registration requires a host-tracked hot-module lifetime.");
    }

    private sealed class DelegateHandler<TEvent>(
        Action<EventContext<TEvent>> callback,
        EventPriority priority,
        bool invokeWhenCancelled) : IAsyncEventHandler<TEvent>
    {
        private Action<EventContext<TEvent>>? callback = callback;

        public EventPriority Priority { get; } = priority;

        public bool InvokeWhenCancelled { get; } = invokeWhenCancelled;

        public ValueTask HandleAsync(EventContext<TEvent> context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Read(ref callback)?.Invoke(context);
            return ValueTask.CompletedTask;
        }

        public void Deactivate() => Interlocked.Exchange(ref callback, null);
    }

    private sealed class DelegateMonitor<TEvent>(
        Action<EventOutcome<TEvent>> callback,
        EventPriority priority) : IAsyncEventMonitor<TEvent>
    {
        private Action<EventOutcome<TEvent>>? callback = callback;

        public EventPriority Priority { get; } = priority;

        public ValueTask ObserveAsync(EventOutcome<TEvent> outcome, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Read(ref callback)?.Invoke(outcome);
            return ValueTask.CompletedTask;
        }

        public void Deactivate() => Interlocked.Exchange(ref callback, null);
    }

    private sealed class EventRegistration(IDisposable registration, Action deactivate) : IDisposable
    {
        private IDisposable? registration = registration;
        private Action? deactivate = deactivate;

        public void Dispose()
        {
            Interlocked.Exchange(ref deactivate, null)?.Invoke();
            Interlocked.Exchange(ref registration, null)?.Dispose();
        }
    }
}
