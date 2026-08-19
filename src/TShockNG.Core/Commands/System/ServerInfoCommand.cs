using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;
using TShockNG.Contracts.Operations;

namespace TShockNG.Core.Commands.System;

public sealed class ServerInfoCommand(IHostMetricsReader metricsReader) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "serverinfo",
        Array.Empty<string>(),
        ["tshock.info"]);

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        HostMetricsSnapshot metrics = metricsReader.Read();

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.serverinfo.memory", metrics.WorkingSetBytes),
            MessageDescriptor.Info("command.serverinfo.virtual-memory", metrics.VirtualMemoryBytes),
            MessageDescriptor.Info("command.serverinfo.processor-time", metrics.TotalProcessorTime),
            MessageDescriptor.Info("command.serverinfo.operating-system", metrics.OperatingSystem),
            MessageDescriptor.Info("command.serverinfo.processor-count", metrics.ProcessorCount),
            MessageDescriptor.Info("command.serverinfo.machine-name", metrics.MachineName)));
    }
}
