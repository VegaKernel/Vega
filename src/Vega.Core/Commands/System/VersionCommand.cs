using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands.System;

public sealed class VersionCommand(string productName, string version) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "version",
        Array.Empty<string>(),
        ["vega.cfg.maintenance"],
        Syntax: "version");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            const string code = "command.version.usage";
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                code,
                [MessageDescriptor.Error(code).WithDefaultText("Usage: version.")]));
        }

        string product = string.IsNullOrWhiteSpace(productName) ? "Vega" : productName.Trim();
        string value = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.version.current", product, value)
                .WithDefaultText("{0} version {1}.")));
    }
}
