using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Worlds;

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

    public bool CanMovePlayersBetweenWorlds { get; init; }
}

public interface IOperationsSnapshotProvider
{
    OperationsSnapshot Capture(OperationsLogFilter? logFilter = null);
}
