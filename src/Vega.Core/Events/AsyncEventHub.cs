using Vega.Contracts.Events;

namespace Vega.Core.Events;

public sealed class AsyncEventHub : IAsyncEventHub
{
    private readonly object sync = new();
    private readonly Dictionary<Type, object> buckets = [];

    public IDisposable Register<TEvent>(IAsyncEventHandler<TEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return GetBucket<TEvent>().Register(handler);
    }

    public IDisposable Monitor<TEvent>(IAsyncEventMonitor<TEvent> monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return GetBucket<TEvent>().Monitor(monitor);
    }

    public ValueTask<EventOutcome<TEvent>> DispatchAsync<TEvent>(
        TEvent value,
        CancellationToken cancellationToken = default)
    {
        Bucket<TEvent>? bucket;
        lock (sync)
        {
            bucket = buckets.TryGetValue(typeof(TEvent), out object? valueBucket)
                ? (Bucket<TEvent>)valueBucket
                : null;
        }

        if (bucket is null)
        {
            return ValueTask.FromResult(new EventOutcome<TEvent>(value, null));
        }

        (IAsyncEventHandler<TEvent>[] handlers, IAsyncEventMonitor<TEvent>[] monitors) = bucket.Snapshot();
        return new AsyncEventPipeline<TEvent>(handlers, monitors).DispatchAsync(value, cancellationToken);
    }

    private Bucket<TEvent> GetBucket<TEvent>()
    {
        lock (sync)
        {
            if (buckets.TryGetValue(typeof(TEvent), out object? existing))
            {
                return (Bucket<TEvent>)existing;
            }

            var created = new Bucket<TEvent>();
            buckets.Add(typeof(TEvent), created);
            return created;
        }
    }

    private sealed class Bucket<TEvent>
    {
        private readonly object sync = new();
        private readonly List<IAsyncEventHandler<TEvent>> handlers = [];
        private readonly List<IAsyncEventMonitor<TEvent>> monitors = [];

        public IDisposable Register(IAsyncEventHandler<TEvent> handler)
        {
            lock (sync)
            {
                handlers.Add(handler);
            }

            return new Registration(() =>
            {
                lock (sync)
                {
                    handlers.Remove(handler);
                }
            });
        }

        public IDisposable Monitor(IAsyncEventMonitor<TEvent> monitor)
        {
            lock (sync)
            {
                monitors.Add(monitor);
            }

            return new Registration(() =>
            {
                lock (sync)
                {
                    monitors.Remove(monitor);
                }
            });
        }

        public (IAsyncEventHandler<TEvent>[] Handlers, IAsyncEventMonitor<TEvent>[] Monitors) Snapshot()
        {
            lock (sync)
            {
                return (handlers.ToArray(), monitors.ToArray());
            }
        }
    }

    private sealed class Registration(Action release) : IDisposable
    {
        private Action? release = release;

        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
