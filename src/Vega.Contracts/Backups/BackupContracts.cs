namespace Vega.Contracts.Backups;

public enum DeploymentBackupState
{
    Disabled,
    Idle,
    SavingWorld,
    CapturingDatabase,
    Archiving,
    Succeeded,
    Failed
}

public sealed record DeploymentBackupSnapshot(
    bool Enabled,
    DeploymentBackupState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? NextScheduledAt,
    string? ArchivePath,
    long ArchiveBytes,
    string? Detail)
{
    public static DeploymentBackupSnapshot Disabled { get; } = new(
        false,
        DeploymentBackupState.Disabled,
        null,
        null,
        null,
        null,
        0,
        null);
}

public interface IDeploymentBackupService
{
    DeploymentBackupSnapshot Read();

    ValueTask<DeploymentBackupSnapshot> CreateAsync(CancellationToken cancellationToken = default);
}

public interface IDatabaseBackupSource
{
    string Provider { get; }

    int SchemaVersion { get; }

    string BackupFileName { get; }

    ValueTask WriteBackupAsync(string destinationPath, CancellationToken cancellationToken = default);
}

public interface IBackupConsistencyBarrier
{
    ValueTask SaveAndFlushAsync(CancellationToken cancellationToken = default);
}
