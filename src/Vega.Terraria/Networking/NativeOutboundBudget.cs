namespace Vega.Terraria.Networking;

/// <summary>Per-world process cap shared by all candidate TCP connections.</summary>
internal sealed class NativeOutboundBudget
{
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
        // Each credit is exact; a concurrent reader may sample the two counters
        // at slightly different instants. Only diagnostics/pressure hints use both.
        get => (Volatile.Read(ref outstanding), Volatile.Read(ref retained));
    }

    public NativeOutboundEnqueueResult TryReserve(int bytes)
    {
        while (true)
        {
            int current = Volatile.Read(ref outstanding);
            if (current >= maxEntries) return NativeOutboundEnqueueResult.EntryLimit;
            if (Interlocked.CompareExchange(ref outstanding, current + 1, current) == current) break;
        }
        if (bytes == 0 || TryReserveBytes(bytes)) return NativeOutboundEnqueueResult.Accepted;
        // A failed byte reservation never leaves an entry charged. Both caps are
        // enforced independently, including in-progress reservations.
        ReleaseEntries(1);
        return NativeOutboundEnqueueResult.ByteLimit;
    }

    /// <summary>Charge a temporary coalescing rental without claiming another request credit.</summary>
    public bool TryReserveBytes(int bytes)
    {
        while (true)
        {
            long current = Volatile.Read(ref retained);
            if (bytes > maxBytes - current) return false;
            if (Interlocked.CompareExchange(ref retained, current + bytes, current) == current) return true;
        }
    }

    public void ReleaseBytes(long bytes) => Interlocked.Add(ref retained, -bytes);
    public void ReleaseEntries(int count) => Interlocked.Add(ref outstanding, -count);
}
