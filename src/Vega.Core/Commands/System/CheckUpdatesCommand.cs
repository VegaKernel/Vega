using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;
using TShockNG.Contracts.Updates;

namespace TShockNG.Core.Commands.System;

public sealed class CheckUpdatesCommand(IUpdateChecker updateChecker) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "checkupdates",
        Array.Empty<string>(),
        ["tshock.cfg.maintenance"]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            await updateChecker.CheckAsync(cancellationToken);
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.checkupdates.completed")
                    .WithDefaultText("Update check completed. Administrators were notified if an update is available."));
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
