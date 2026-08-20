using System.Diagnostics;
using Vega.Contracts.Operations;

namespace Vega.Core.Operations;

public sealed class ProcessHostMetricsReader : IHostMetricsReader
{
    public HostMetricsSnapshot Read()
    {
        using Process process = Process.GetCurrentProcess();
        return new HostMetricsSnapshot(
            process.WorkingSet64,
            process.VirtualMemorySize64,
            process.TotalProcessorTime,
            Environment.OSVersion.ToString(),
            Environment.ProcessorCount,
            Environment.MachineName);
    }
}
