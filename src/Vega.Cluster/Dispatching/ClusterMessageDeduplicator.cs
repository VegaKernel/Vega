using Vega.Contracts.Cluster;

namespace Vega.Cluster.Dispatching;

public sealed class ClusterMessageDeduplicator
{
    private readonly object sync = new();
    private readonly Dictionary<ClusterMessageId, DateTimeOffset> accepted = [];
    private readonly int capacity;
    private readonly TimeSpan retention;
    private readonly TimeProvider time;

    public ClusterMessageDeduplicator(
        int capacity = 10_000,
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

    public bool TryAccept(ClusterMessageId messageId)
    {
        lock (sync)
        {
            DateTimeOffset now = time.GetUtcNow();
            foreach (ClusterMessageId expired in accepted
                .Where(pair => pair.Value <= now)
                .Select(static pair => pair.Key)
                .ToArray())
            {
                accepted.Remove(expired);
            }

            if (accepted.ContainsKey(messageId))
            {
                return false;
            }

            if (accepted.Count >= capacity)
            {
                ClusterMessageId oldest = accepted.MinBy(static pair => pair.Value).Key;
                accepted.Remove(oldest);
            }

            accepted.Add(messageId, now + retention);
            return true;
        }
    }

    public void Forget(ClusterMessageId messageId)
    {
        lock (sync)
        {
            accepted.Remove(messageId);
        }
    }
}
