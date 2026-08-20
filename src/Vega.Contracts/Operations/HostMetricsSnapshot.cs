namespace Vega.Contracts.Operations;

public sealed record HostMetricsSnapshot(
    long WorkingSetBytes,
    long VirtualMemoryBytes,
    TimeSpan TotalProcessorTime,
    string OperatingSystem,
    int ProcessorCount,
    string MachineName);

public interface IHostMetricsReader
{
    HostMetricsSnapshot Read();
}
