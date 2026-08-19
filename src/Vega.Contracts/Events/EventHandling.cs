namespace Vega.Contracts.Events;

public sealed class EventContext<TEvent>
{
    public EventContext(TEvent value)
    {
        Value = value;
    }

    public TEvent Value { get; }

    public bool IsCancelled => Cancellation is not null;

    public EventCancellation? Cancellation { get; private set; }

    public bool Cancel(EventCancellation cancellation)
    {
        if (Cancellation is not null)
        {
            return false;
        }

        Cancellation = cancellation;
        return true;
    }

    public EventOutcome<TEvent> ToOutcome() => new(Value, Cancellation);
}

public readonly record struct EventOutcome<TEvent>(
    TEvent Value,
    EventCancellation? Cancellation)
{
    public bool IsCancelled => Cancellation is not null;
}

public interface IEventHandler<TEvent>
{
    EventPriority Priority => EventPriority.Normal;

    bool InvokeWhenCancelled => false;

    void Handle(EventContext<TEvent> context);
}

public interface IEventMonitor<TEvent>
{
    EventPriority Priority => EventPriority.Normal;

    void Observe(EventOutcome<TEvent> outcome);
}

public interface IAsyncEventHandler<TEvent>
{
    EventPriority Priority => EventPriority.Normal;

    bool InvokeWhenCancelled => false;

    ValueTask HandleAsync(EventContext<TEvent> context, CancellationToken cancellationToken);
}

public interface IAsyncEventMonitor<TEvent>
{
    EventPriority Priority => EventPriority.Normal;

    ValueTask ObserveAsync(EventOutcome<TEvent> outcome, CancellationToken cancellationToken);
}

public interface IEventPipeline<TEvent>
{
    EventOutcome<TEvent> Dispatch(TEvent value);
}

public interface IAsyncEventPipeline<TEvent>
{
    ValueTask<EventOutcome<TEvent>> DispatchAsync(TEvent value, CancellationToken cancellationToken = default);
}
