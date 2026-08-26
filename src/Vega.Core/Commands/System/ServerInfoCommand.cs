using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.System;

public sealed class ServerInfoCommand(IHostMetricsReader metricsReader) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "serverinfo",
        ["version"],
        ["vega.info"],
        Syntax: "serverinfo | version");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (string.Equals(invocation.Name, "version", StringComparison.OrdinalIgnoreCase))
        {
            if (invocation.Arguments.Count != 0)
            {
                const string code = "command.version.usage";
                return ValueTask.FromResult(new CommandExecutionResult(
                    CommandExecutionStatus.InvalidUsage,
                    code,
                    [MessageDescriptor.Error(code).WithDefaultText("Usage: version.")]));
            }

            string version = typeof(ServerInfoCommand).Assembly.GetName().Version?.ToString() ?? "unknown";
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.version.current", version)
                    .WithDefaultText("Vega version {0}.")));
        }

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
