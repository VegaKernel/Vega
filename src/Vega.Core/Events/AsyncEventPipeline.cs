using Vega.Contracts.Events;

namespace Vega.Core.Events;

public sealed class AsyncEventPipeline<TEvent> : IAsyncEventPipeline<TEvent>
{
    private readonly IAsyncEventHandler<TEvent>[] handlers;
    private readonly IAsyncEventMonitor<TEvent>[] monitors;

    public AsyncEventPipeline(
        IEnumerable<IAsyncEventHandler<TEvent>> handlers,
        IEnumerable<IAsyncEventMonitor<TEvent>>? monitors = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        this.handlers = handlers
            .Select((handler, index) => new { Handler = handler, Index = index })
            .OrderBy(entry => entry.Handler.Priority)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Handler)
            .ToArray();

        this.monitors = (monitors ?? [])
            .Select((monitor, index) => new { Monitor = monitor, Index = index })
            .OrderBy(entry => entry.Monitor.Priority)
            .ThenBy(entry => entry.Index)
            .Select(entry => entry.Monitor)
            .ToArray();
    }

    public async ValueTask<EventOutcome<TEvent>> DispatchAsync(
        TEvent value,
        CancellationToken cancellationToken = default)
    {
        var context = new EventContext<TEvent>(value);

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context.IsCancelled && !handler.InvokeWhenCancelled)
            {
                continue;
            }

            await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var outcome = context.ToOutcome();
        foreach (var monitor in monitors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await monitor.ObserveAsync(outcome, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }
}
