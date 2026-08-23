using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.Administration;

public sealed class SoftRestartCommand(
    ISoftRestartService restart,
    IOperationsLogger? logger = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "softrestart",
        ["restartmodules", "modules"],
        ["vega.server.soft-restart"],
        Syntax: "softrestart confirm | softrestart <module> [module...] | modules restart ...");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = invocation.Arguments;
        if (string.Equals(invocation.Name, "modules", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count == 0 || !string.Equals(arguments[0], "restart", StringComparison.OrdinalIgnoreCase))
            {
                return new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "command.softrestart.usage",
                    [MessageDescriptor.Error("command.softrestart.usage")
                        .WithDefaultText("Usage: modules restart confirm | modules restart <module> [module...]")]);
            }

            arguments = arguments.Skip(1).ToArray();
        }

        if (arguments.Count == 0)
        {
            return new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "command.softrestart.confirmation-required",
                [MessageDescriptor.Error("command.softrestart.confirmation-required")
                    .WithDefaultText("Use 'softrestart confirm' for all modules, or name one or more modules explicitly.")]);
        }

        IReadOnlySet<string>? modules = string.Equals(arguments[0], "confirm", StringComparison.OrdinalIgnoreCase)
            ? null
            : arguments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        logger?.Log(OperationsLogLevel.Warning, "soft-restart",
            $"Soft restart requested by {invocation.Principal.Name}; target={(modules is null ? "all" : string.Join(',', modules))}.");
        SoftRestartResult result = await restart.RestartAsync(modules, cancellationToken).ConfigureAwait(false);
        logger?.Log(
            result.Status == SoftRestartStatus.Succeeded ? OperationsLogLevel.Information : OperationsLogLevel.Warning,
            "soft-restart",
            $"Soft restart requested by {invocation.Principal.Name} completed with {result.Status}: {result.Message}");
        bool success = result.Status is SoftRestartStatus.Succeeded or SoftRestartStatus.RolledBack;
        return success
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.softrestart.completed").WithDefaultText(result.Message))
            : CommandExecutionResult.Failed("command.softrestart.failed",
                MessageDescriptor.Error("command.softrestart.failed").WithDefaultText(result.Message));
    }
}
