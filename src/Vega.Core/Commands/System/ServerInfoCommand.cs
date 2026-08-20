using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.System;

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
            MessageDescriptor.Info("command.serverinfo.memory", metrics.WorkingSetBytes)
                .WithDefaultText("Working set: {0:N0} bytes"),
            MessageDescriptor.Info("command.serverinfo.virtual-memory", metrics.VirtualMemoryBytes)
                .WithDefaultText("Virtual memory: {0:N0} bytes"),
            MessageDescriptor.Info("command.serverinfo.processor-time", metrics.TotalProcessorTime)
                .WithDefaultText("Processor time: {0}"),
            MessageDescriptor.Info("command.serverinfo.operating-system", metrics.OperatingSystem)
                .WithDefaultText("Operating system: {0}"),
            MessageDescriptor.Info("command.serverinfo.processor-count", metrics.ProcessorCount)
                .WithDefaultText("Processor count: {0}"),
            MessageDescriptor.Info("command.serverinfo.machine-name", metrics.MachineName)
                .WithDefaultText("Machine name: {0}")));
    }
}
