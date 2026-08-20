using System.Diagnostics;
using Vega.Contracts.Operations;

namespace Vega.Operations.Metrics;

public sealed class SampledRuntimeMetricsReader(IHostMetricsReader hostMetrics) : IRuntimeMetricsReader
{
    public RuntimeMetricsSnapshot Read()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out int availableWorkers, out _);
        ThreadPool.GetMaxThreads(out int maximumWorkers, out _);

        return new RuntimeMetricsSnapshot(
            hostMetrics.Read(),
            GC.GetTotalMemory(false),
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            availableWorkers,
            maximumWorkers,
            DateTimeOffset.UtcNow);
    }
}

public sealed class NetworkMetricsCounter : INetworkMetricsReader, INetworkMetricsRecorder
{
    private long inboundPackets;
    private long inboundBytes;
    private long outboundPackets;
    private long outboundBytes;

    public void RecordInbound(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Interlocked.Increment(ref inboundPackets);
        Interlocked.Add(ref inboundBytes, byteCount);
    }

    public void RecordOutbound(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        Interlocked.Increment(ref outboundPackets);
        Interlocked.Add(ref outboundBytes, byteCount);
    }

    public NetworkMetricsSnapshot Read() => new(
        Interlocked.Read(ref inboundPackets),
        Interlocked.Read(ref inboundBytes),
        Interlocked.Read(ref outboundPackets),
        Interlocked.Read(ref outboundBytes),
        DateTimeOffset.UtcNow);
}
