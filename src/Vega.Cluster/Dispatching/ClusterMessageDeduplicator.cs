using Vega.Contracts.Cluster;

namespace Vega.Cluster.Dispatching;

public sealed class ClusterMessageDeduplicator
{
    private readonly object sync = new();
    private readonly Dictionary<(ServerId Sender, ClusterMessageId Id), LinkedListNode<Entry>> accepted = [];
    private readonly LinkedList<Entry> order = new();
    private sealed record Entry((ServerId Sender, ClusterMessageId Id) Key, DateTimeOffset ExpiresAt);
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

    public bool TryAccept(ClusterMessageId messageId) => TryAccept(default, messageId);

    public bool TryAccept(ServerId sender, ClusterMessageId messageId)
    {
        lock (sync)
        {
            DateTimeOffset now = time.GetUtcNow();
            // Insertion order gives constant-time admission/eviction in the usual nondecreasing clock case.
            while (order.First is { } expired && expired.Value.ExpiresAt <= now) Remove(expired);

            var key = (sender, messageId);
            if (accepted.TryGetValue(key, out var existing))
            {
                if (existing.Value.ExpiresAt > now) return false;
                Remove(existing);
            }

            if (accepted.Count >= capacity) Remove(order.First!);
            accepted.Add(key, order.AddLast(new Entry(key, now + retention)));
            return true;
        }
    }

    public void Forget(ClusterMessageId messageId) => Forget(default, messageId);

    public void Forget(ServerId sender, ClusterMessageId messageId)
    {
        lock (sync)
        {
            if (accepted.TryGetValue((sender, messageId), out var node)) Remove(node);
        }
    }

    private void Remove(LinkedListNode<Entry> entry)
    {
        accepted.Remove(entry.Value.Key);
        order.Remove(entry);
    }
}
