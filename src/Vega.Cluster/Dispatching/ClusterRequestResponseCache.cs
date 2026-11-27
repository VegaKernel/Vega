using Vega.Contracts.Cluster;

namespace Vega.Cluster.Dispatching;

internal sealed class ClusterRequestResponseCache
{
    private readonly object sync = new();
    private readonly Dictionary<(ServerId Sender, ClusterMessageId Id), Entry> responses = [];
    private readonly int capacity;
    private readonly TimeSpan retention;
    private readonly TimeProvider time;

    public ClusterRequestResponseCache(
        int capacity = 1_000,
        TimeSpan? retention = null,
        TimeProvider? timeProvider = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        this.capacity = capacity;
        this.retention = retention ?? TimeSpan.FromMinutes(10);
        time = timeProvider ?? TimeProvider.System;
    }

    public bool TryGet<TResponse>(ServerId sender, ClusterMessageId messageId, out TResponse? response)
        where TResponse : IClusterResponse
    {
        lock (sync)
        {
            PurgeExpired(time.GetUtcNow());
            if (responses.TryGetValue((sender, messageId), out Entry? entry) && entry.Response is TResponse typed)
            {
                response = typed;
                return true;
            }

            response = default;
            return false;
        }
    }

    public void Store<TResponse>(ServerId sender, ClusterMessageId messageId, TResponse response)
        where TResponse : IClusterResponse
    {
        lock (sync)
        {
            DateTimeOffset now = time.GetUtcNow();
            PurgeExpired(now);
            var key = (sender, messageId);
            if (responses.Count >= capacity && !responses.ContainsKey(key))
            {
                var oldest = responses.MinBy(static pair => pair.Value.ExpiresAt).Key;
                responses.Remove(oldest);
            }

            responses[key] = new Entry(response, now + retention);
        }
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var expired in responses
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            responses.Remove(expired);
        }
    }

    private sealed record Entry(IClusterResponse Response, DateTimeOffset ExpiresAt);
}
