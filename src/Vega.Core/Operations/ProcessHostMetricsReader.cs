using System.Diagnostics;
using TShockNG.Contracts.Operations;

namespace TShockNG.Core.Operations;

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
