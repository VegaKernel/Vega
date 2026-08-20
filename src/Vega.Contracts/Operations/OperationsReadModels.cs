using Vega.Contracts.Players;
using Vega.Contracts.Runtime;

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
    string? Failure = null);

public interface IModuleOperationsCatalog
{
    IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules();
}

public sealed record ServerRuntimeStateSnapshot(
    string? WorldName,
    int MaximumPlayers,
    int ListenerPort,
    bool IsWorldReady,
    double TicksPerSecond,
    DateTimeOffset SampledAt);

public interface IServerRuntimeStateReader
{
    ServerRuntimeStateSnapshot Read();
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
    IReadOnlyList<string> Warnings);

public sealed record OperationsSnapshot(
    ServerDashboardSnapshot Server,
    IReadOnlyList<PlayerSnapshot> Players,
    IReadOnlyList<ModuleOperationsSnapshot> Modules,
    IReadOnlyList<OperationsLogRecord> Logs,
    RuntimeMetricsSnapshot Runtime,
    NetworkMetricsSnapshot Network);

public interface IOperationsSnapshotProvider
{
    OperationsSnapshot Capture(OperationsLogFilter? logFilter = null);
}
