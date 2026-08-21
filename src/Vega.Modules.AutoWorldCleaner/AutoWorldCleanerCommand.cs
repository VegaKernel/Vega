using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Modules.AutoWorldCleaner;

public sealed class AutoWorldCleanerCommand(AutoWorldCleanerModule cleaner) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "worldclean",
        ["cleanworld"],
        ["tshock.cfg.maintenance"]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            return Status();
        }

        if (invocation.Arguments.Count > 1 ||
            (invocation.Arguments.Count == 1 &&
             !string.Equals(invocation.Arguments[0], "run", StringComparison.OrdinalIgnoreCase)))
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.worldclean.usage",
                [MessageDescriptor.Error("command.worldclean.usage")
                    .WithDefaultText("Usage: worldclean [run|status]")]);
        }

        AutoWorldCleanerRunResult result = await cleaner
            .RunNowAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        return result.Status switch
        {
            AutoWorldCleanerRunStatus.Applied => CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.worldclean.completed",
                        result.RemovedItems,
                        result.RemovedProjectiles)
                    .WithDefaultText("World cleanup completed: items={0}, projectiles={1}.")),
            AutoWorldCleanerRunStatus.SkippedNotReady => CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.worldclean.not-ready")
                    .WithDefaultText("World cleanup skipped because the world is not ready.")),
            AutoWorldCleanerRunStatus.SkippedNoPlayers => CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.worldclean.no-players")
                    .WithDefaultText("World cleanup skipped because no players are online.")),
            AutoWorldCleanerRunStatus.Busy => CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.worldclean.busy")
                    .WithDefaultText("World cleanup is already running.")),
            AutoWorldCleanerRunStatus.Cancelled => CommandExecutionResult.Failed(
                "command.worldclean.cancelled",
                MessageDescriptor.Error("command.worldclean.cancelled")
                    .WithDefaultText("World cleanup was cancelled.")),
            AutoWorldCleanerRunStatus.Disabled => CommandExecutionResult.Failed(
                "command.worldclean.disabled",
                MessageDescriptor.Error("command.worldclean.disabled")
                    .WithDefaultText("AutoWorldCleaner is disabled in config/auto-world-cleaner.json.")),
            _ => CommandExecutionResult.Failed(
                result.ErrorCode ?? "command.worldclean.failed",
                MessageDescriptor.Error("command.worldclean.failed")
                    .WithDefaultText("World cleanup failed."))
        };
    }

    private CommandExecutionResult Status()
    {
        WorldCleanupStatusSnapshot status = cleaner.Read();
        string last = status.LastCompletedAt?.ToString("O") ?? "never";
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.worldclean.status")
                .WithDefaultText(
                    $"AutoWorldCleaner: enabled={status.Enabled.ToString().ToLowerInvariant()}, " +
                    $"state={status.State}, last={last}, items={status.LastRemovedItems}, " +
                    $"projectiles={status.LastRemovedProjectiles}."));
    }
}
