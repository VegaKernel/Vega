using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Operations;

namespace TShockNG.Core.Commands.System;

public sealed class ServerInfoCommand(IHostMetricsReader metricsReader) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "serverinfo",
        Array.Empty<string>(),
        ["tshock.cfg.serverinfo"]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        HostMetricsSnapshot metrics = metricsReader.Read();

        await invocation.Output.InfoAsync($"Memory usage: {metrics.WorkingSetBytes}", cancellationToken);
        await invocation.Output.InfoAsync($"Allocated memory: {metrics.VirtualMemoryBytes}", cancellationToken);
        await invocation.Output.InfoAsync($"Total processor time: {metrics.TotalProcessorTime}", cancellationToken);
        await invocation.Output.InfoAsync($"Operating system: {metrics.OperatingSystem}", cancellationToken);
        await invocation.Output.InfoAsync($"Proc count: {metrics.ProcessorCount}", cancellationToken);
        await invocation.Output.InfoAsync($"Machine name: {metrics.MachineName}", cancellationToken);

        return CommandExecutionResult.Success;
    }
}
