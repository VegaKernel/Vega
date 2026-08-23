using Vega.Contracts.Backups;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Backups;

public sealed class BackupCommand(IDeploymentBackupService backups) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "backup",
        ["backupnow"],
        ["vega.server.backup"],
        Syntax: "backup | backup status");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count > 1 ||
            invocation.Arguments.Count == 1 && !string.Equals(invocation.Arguments[0], "status", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.backup.usage",
                [MessageDescriptor.Error("command.backup.usage").WithDefaultText("Usage: backup | backup status")]);
        }

        DeploymentBackupSnapshot status = invocation.Arguments.Count == 1
            ? backups.Read()
            : await backups.CreateAsync(cancellationToken).ConfigureAwait(false);
        bool success = status.State is DeploymentBackupState.Idle or DeploymentBackupState.Succeeded or DeploymentBackupState.Disabled;
        string text = $"Backup: {status.State}; archive={status.ArchivePath ?? "-"}; bytes={status.ArchiveBytes}; detail={status.Detail ?? "-"}";
        return success
            ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.backup.status").WithDefaultText(text))
            : CommandExecutionResult.Failed(
                "command.backup.failed",
                MessageDescriptor.Error("command.backup.failed").WithDefaultText(text));
    }
}
