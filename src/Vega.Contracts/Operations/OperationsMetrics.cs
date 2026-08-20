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

public sealed record WorldLoadMetricsSnapshot(
    bool CacheHit,
    string CacheReason,
    long SourceBytes,
    long ImageBytes,
    TimeSpan FileReadDuration,
    TimeSpan TileDecodeDuration,
    TimeSpan ChestCreationDuration,
    TimeSpan SignCreationDuration,
    TimeSpan NpcCreationDuration,
    TimeSpan TileEntityCreationDuration,
    TimeSpan PostLoadInitializationDuration,
    TimeSpan WldLoadDuration,
    TimeSpan WorldImageLoadDuration,
    TimeSpan WorldImageBuildDuration,
    TimeSpan WorldReadyDuration,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    DateTimeOffset SampledAt)
{
    public TimeSpan CollectionsDuration =>
        ChestCreationDuration + SignCreationDuration + NpcCreationDuration + TileEntityCreationDuration;
}

public interface IWorldLoadMetricsReader
{
    WorldLoadMetricsSnapshot Read();
}
