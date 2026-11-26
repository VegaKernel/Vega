using Vega.Contracts.Players;
using Vega.Contracts.Bans;
using Vega.Contracts.Moderation;
using Vega.Contracts.Networking;
using Vega.Contracts.Runtime;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Contracts.Scenes;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Backups;

namespace Vega.Contracts.Operations;

public enum ModuleLoadState
{
    Registered,
    Loaded,
    Degraded,
    Failed,
    Disabled
}

public sealed record ModuleOperationsSnapshot(
    string Name,
    string Version,
    ModuleLoadState State,
    IReadOnlyList<string> Dependencies,
    TimeSpan StartupDuration,
    string? Failure = null)
{
    public bool IsHotReloadable { get; init; }
    public long FaultCount { get; init; }
    public string? LastFaultOperation { get; init; }
    public string? LastFaultSummary { get; init; }
}

public interface IModuleOperationsCatalog
{
    IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules();
}

public sealed record ModuleDirectoryStatusSnapshot(
    bool IsMonitored,
    bool IsHotReloadEnabled,
    bool IsRestartRequired,
    DateTimeOffset? ChangedAt,
    IReadOnlyList<string> ChangedFiles,
    string? Failure = null)
{
    public static ModuleDirectoryStatusSnapshot Unmonitored { get; } = new(
        false,
        false,
        false,
        null,
        Array.Empty<string>());
}

public interface IModuleDirectoryStatusReader
{
    ModuleDirectoryStatusSnapshot Read();
}


public enum WorldCleanupRunState
{
    Disabled,
    Idle,
    Running,
    Succeeded,
    Skipped,
    Failed
}

public sealed record WorldCleanupStatusSnapshot(
    WorldId WorldId,
    bool Enabled,
    WorldCleanupRunState State,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastCompletedAt,
    int LastRemovedItems,
    int LastRemovedProjectiles,
    string? Detail = null)
{
    public static WorldCleanupStatusSnapshot Disabled { get; } = new(
        WorldId.Primary,
        false,
        WorldCleanupRunState.Disabled,
        null,
        null,
        0,
        0);
}

public interface IWorldCleanupStatusReader
{
    WorldCleanupStatusSnapshot Read();
}

public enum WorldContextKind
{
    Primary,
    Sandbox
}

public enum WorldContextSimulationState
{
    Stopped,
    Running
}

public sealed record WorldContextSnapshot(
    WorldId Id,
    string DisplayName,
    WorldContextKind Kind,
    WorldContextSimulationState SimulationState,
    IReadOnlyList<PlayerId> PlayerIds,
    int WorldEpoch,
    string Ruleset,
    TimeSpan TimeToLiveRemaining)
{
    public SandboxStatus? Sandbox { get; init; }
}

public interface IWorldContextOperationsCatalog
{
    bool CanMovePlayers { get; }

    IReadOnlyList<WorldContextSnapshot> SnapshotContexts();
}

public sealed record ServerRuntimeStateSnapshot(
    string? WorldName,
    int MaximumPlayers,
    int ListenerPort,
    bool IsWorldReady,
    double TicksPerSecond,
    DateTimeOffset SampledAt)
{
    public TickRateStatisticsSnapshot TickRate { get; init; } = TickRateStatisticsSnapshot.Empty;
}

public sealed record TickRateStatisticsSnapshot(
    double Current,
    double AverageOneMinute,
    double Minimum,
    DateTimeOffset? MinimumAt,
    double Maximum,
    DateTimeOffset? MaximumAt,
    int SampleCount,
    double TickDurationP50Milliseconds = 0,
    double TickDurationP95Milliseconds = 0,
    double TickDurationP99Milliseconds = 0)
{
    /// <summary>CPU utilization of the authoritative thread relative to one logical core; null until sampled.</summary>
    public double? GameThreadCpuPercent { get; init; }

    public static TickRateStatisticsSnapshot Empty { get; } = new(
        0,
        0,
        0,
        null,
        0,
        null,
        0);
}

public interface IServerRuntimeStateReader
{
    ServerRuntimeStateSnapshot Read();
}

public sealed record StartupProgressSnapshot(
    string Activity,
    double? Fraction,
    string RawText,
    DateTimeOffset SampledAt);

public interface IStartupProgressReader
{
    StartupProgressSnapshot Read();
}

public sealed record ServerDashboardSnapshot(
    string ServerName,
    string Version,
    DateTimeOffset StartedAt,
    TimeSpan Uptime,
    RuntimeLifecycleStage LifecycleStage,
    string? WorldName,
    bool IsWorldReady,
    bool IsNetworkReady,
    int OnlinePlayers,
    int MaximumPlayers,
    int ListenerPort,
    double TicksPerSecond,
    IReadOnlyList<string> Warnings)
{
    public TickRateStatisticsSnapshot TickRate { get; init; } = TickRateStatisticsSnapshot.Empty;
}

public sealed record OperationsSnapshot(
    ServerDashboardSnapshot Server,
    IReadOnlyList<PlayerSnapshot> Players,
    IReadOnlyList<ModuleOperationsSnapshot> Modules,
    IReadOnlyList<OperationsLogRecord> Logs,
    RuntimeMetricsSnapshot Runtime,
    NetworkMetricsSnapshot Network)
{
    public WorldLoadMetricsSnapshot? WorldLoad { get; init; }

    public ModuleDirectoryStatusSnapshot ModuleDirectory { get; init; } =
        ModuleDirectoryStatusSnapshot.Unmonitored;

    public IReadOnlyList<WorldClockSnapshot> WorldClocks { get; init; } =
        Array.Empty<WorldClockSnapshot>();

    public IReadOnlyList<OperationsLogRecord> Chat { get; init; } =
        Array.Empty<OperationsLogRecord>();

    public IReadOnlyList<WorldContextSnapshot> WorldContexts { get; init; } =
        Array.Empty<WorldContextSnapshot>();

    public WorldCleanupStatusSnapshot WorldCleanup { get; init; } =
        WorldCleanupStatusSnapshot.Disabled;

    public bool CanMovePlayersBetweenWorlds { get; init; }

    public IReadOnlyList<SecuritySignal> SecuritySignals { get; init; } =
        Array.Empty<SecuritySignal>();

    public IReadOnlyList<RegionSnapshot> Regions { get; init; } =
        Array.Empty<RegionSnapshot>();

    public IReadOnlyList<BanSnapshot> Bans { get; init; } =
        Array.Empty<BanSnapshot>();

    public IReadOnlyList<MuteSnapshot> Mutes { get; init; } =
        Array.Empty<MuteSnapshot>();

    public IReadOnlyList<SceneSnapshot> Scenes { get; init; } =
        Array.Empty<SceneSnapshot>();

    public IReadOnlyList<VisibilityLayerSnapshot> VisibilityLayers { get; init; } =
        Array.Empty<VisibilityLayerSnapshot>();

    public DeploymentBackupSnapshot Backup { get; init; } = DeploymentBackupSnapshot.Disabled;
}

public interface IOperationsSnapshotProvider
{
    OperationsSnapshot Capture(
        OperationsLogFilter? logFilter = null,
        OperationsSnapshotExposure exposure = OperationsSnapshotExposure.Redacted);
}

public enum OperationsSnapshotExposure
{
    Redacted,
    Sensitive
}
