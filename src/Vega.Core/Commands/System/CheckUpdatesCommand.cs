using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Updates;

namespace Vega.Core.Commands.System;

public sealed class CheckUpdatesCommand(IUpdateChecker updateChecker) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "checkupdates",
        Array.Empty<string>(),
        ["vega.cfg.maintenance"],
        Syntax: "checkupdates");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            const string code = "command.checkupdates.usage";
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                code,
                [MessageDescriptor.Error(code).WithDefaultText("Usage: checkupdates.")]);
        }

        try
        {
            UpdateCheckResult result = await updateChecker.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (result.LatestVersion is null)
            {
                return CommandExecutionResult.WithMessages(
                    MessageDescriptor.Info("command.checkupdates.no-published-release", result.CurrentVersion)
                        .WithDefaultText("No published Vega release is available yet. Running version: {0}."));
            }

            if (result.IsUpdateAvailable)
            {
                return CommandExecutionResult.WithMessages(
                    MessageDescriptor.Warning(
                            "command.checkupdates.update-available",
                            result.CurrentVersion,
                            result.LatestVersion)
                        .WithDefaultText("Vega update available: {1} (running {0})."));
            }

            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.checkupdates.current",
                        result.CurrentVersion,
                        result.LatestVersion)
                    .WithDefaultText("No newer Vega release found. Running {0}; latest published {1}."));
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
