namespace Vega.Contracts.Operations;

public sealed record RuntimeMetricsSnapshot(
    HostMetricsSnapshot Host,
    long ManagedMemoryBytes,
    long GcHeapBytes,
    long GcFragmentedBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int AvailableWorkerThreads,
    int MaximumWorkerThreads,
    DateTimeOffset SampledAt);

public interface IRuntimeMetricsReader
{
    RuntimeMetricsSnapshot Read();
}

public sealed record NetworkMetricsSnapshot(
    long InboundPackets,
    long InboundBytes,
    long OutboundPackets,
    long OutboundBytes,
    DateTimeOffset SampledAt);

public interface INetworkMetricsReader
{
    NetworkMetricsSnapshot Read();
}

public interface INetworkMetricsRecorder
{
    void RecordInbound(int byteCount);

    void RecordOutbound(int byteCount);
}
