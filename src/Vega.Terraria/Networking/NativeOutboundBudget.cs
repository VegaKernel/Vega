namespace Vega.Terraria.Networking;

/// <summary>Per-world process cap shared by all candidate TCP connections.</summary>
internal sealed class NativeOutboundBudget
{
    private readonly object gate = new();
    private readonly int maxEntries;
    private readonly long maxBytes;
    private int outstanding;
    private long retained;

    public NativeOutboundBudget(int maxEntries, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        this.maxEntries = maxEntries;
        this.maxBytes = maxBytes;
    }

    public (int Entries, long Bytes) Snapshot
    {
        get { lock (gate) return (outstanding, retained); }
    }

    public NativeOutboundEnqueueResult TryReserve(int bytes)
    {
        lock (gate)
        {
            if (outstanding >= maxEntries) return NativeOutboundEnqueueResult.EntryLimit;
            if (bytes > maxBytes - retained) return NativeOutboundEnqueueResult.ByteLimit;
            outstanding++;
            retained += bytes;
            return NativeOutboundEnqueueResult.Accepted;
        }
    }

    /// <summary>Charge a temporary coalescing rental without claiming another request credit.</summary>
    public bool TryReserveBytes(int bytes)
    {
        lock (gate)
        {
            if (bytes > maxBytes - retained) return false;
            retained += bytes;
            return true;
        }
    }

    public void ReleaseBytes(int bytes) { lock (gate) retained -= bytes; }
    public void ReleaseEntries(int count) { lock (gate) outstanding -= count; }
}
