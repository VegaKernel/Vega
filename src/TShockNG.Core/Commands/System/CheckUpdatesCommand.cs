using TShockNG.Contracts.Commands;
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
        await invocation.Output.InfoAsync(
            "An update check has been queued. If an update is available, you will be notified shortly.",
            cancellationToken);

        try
        {
            await updateChecker.CheckAsync(cancellationToken);
            return CommandExecutionResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CommandExecutionResult(CommandExecutionStatus.Failed, "update.check_failed");
        }
    }
}
