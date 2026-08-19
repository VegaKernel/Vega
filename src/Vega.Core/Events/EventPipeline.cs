using Vega.Contracts.Events;

namespace Vega.Core.Events;

public sealed class EventPipeline<TEvent> : IEventPipeline<TEvent>
{
    private readonly IEventHandler<TEvent>[] handlers;
    private readonly IEventMonitor<TEvent>[] monitors;

    public EventPipeline(
        IEnumerable<IEventHandler<TEvent>> handlers,
        IEnumerable<IEventMonitor<TEvent>>? monitors = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        this.handlers = Order(handlers).ToArray();
        this.monitors = Order(monitors ?? []).ToArray();
    }

    public EventOutcome<TEvent> Dispatch(TEvent value)
    {
        var context = new EventContext<TEvent>(value);

        foreach (var handler in handlers)
        {
            if (context.IsCancelled && !handler.InvokeWhenCancelled)
            {
                continue;
            }

            handler.Handle(context);
        }

        var outcome = context.ToOutcome();
        foreach (var monitor in monitors)
        {
            monitor.Observe(outcome);
        }

        return outcome;
    }

    private static IEnumerable<THandler> Order<THandler>(IEnumerable<THandler> source)
        where THandler : class
    {
        return source
            .Select((handler, index) => new { Handler = handler, Index = index })
            .OrderBy(entry => GetPriority(entry.Handler))
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Handler);
    }

    private static EventPriority GetPriority<THandler>(THandler handler) where THandler : class =>
        handler switch
        {
            IEventHandler<TEvent> eventHandler => eventHandler.Priority,
            IEventMonitor<TEvent> monitor => monitor.Priority,
            _ => throw new InvalidOperationException($"Unsupported event registration type {typeof(THandler).FullName}.")
        };
}
