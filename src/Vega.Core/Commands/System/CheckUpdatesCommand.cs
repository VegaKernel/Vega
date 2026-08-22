using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Updates;

namespace Vega.Core.Commands.System;

public sealed class CheckUpdatesCommand(IUpdateChecker updateChecker) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "checkupdates",
        Array.Empty<string>(),
        ["tshock.cfg.maintenance"],
        Syntax: "checkupdates");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            await updateChecker.CheckAsync(cancellationToken);
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.checkupdates.completed")
                    .WithDefaultText("Update check completed."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return CommandExecutionResult.Failed(
                "update.check_failed",
                MessageDescriptor.Error("command.checkupdates.failed")
                    .WithDefaultText("Unable to check for updates."));
        }
    }
}
