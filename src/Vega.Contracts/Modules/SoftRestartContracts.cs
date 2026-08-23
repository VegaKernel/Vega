namespace Vega.Contracts.Modules;

public enum RuntimeComponentLifetime
{
    PreservedInfrastructure,
    RestartableModule,
    ProcessRestartOnly
}

public enum SoftRestartStatus
{
    Succeeded,
    Disabled,
    Busy,
    ModuleNotFound,
    ProcessRestartRequired,
    RolledBack,
    Degraded
}

public sealed record SoftRestartResult(
    SoftRestartStatus Status,
    long PreviousGeneration,
    long ActiveGeneration,
    TimeSpan Duration,
    string Message,
    IReadOnlyList<string> Modules);

public sealed record SoftRestartSnapshot(
    bool Enabled,
    bool IsRestarting,
    long ActiveGeneration,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    SoftRestartStatus? LastStatus,
    string? Detail);

public interface ISoftRestartService
{
    SoftRestartSnapshot Read();

    ValueTask<SoftRestartResult> RestartAsync(
        IReadOnlySet<string>? modules,
        CancellationToken cancellationToken = default);
}

public interface IModuleCallbackLease : IDisposable
{
    long Generation { get; }
}

public sealed record ModuleRestartState(string Key, int Version, ReadOnlyMemory<byte> Payload);

public interface IModuleRestartStateParticipant
{
    string RestartStateKey { get; }

    int RestartStateVersion { get; }

    ModuleRestartState? CaptureRestartState();

    void RestoreRestartState(ModuleRestartState state);
}
