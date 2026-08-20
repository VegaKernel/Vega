using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;

namespace Vega.Operations.ReadModels;

public sealed class OperationsSnapshotProvider(
    string serverName,
    string version,
    DateTimeOffset startedAt,
    IRuntimeLifecycle lifecycle,
    IPlayerDirectory players,
    IServerRuntimeStateReader serverState,
    IModuleOperationsCatalog modules,
    IOperationsLogStore logs,
    IRuntimeMetricsReader runtimeMetrics,
    INetworkMetricsReader networkMetrics,
    IWorldLoadMetricsReader? worldLoadMetrics = null,
    IModuleDirectoryStatusReader? moduleDirectory = null) : IOperationsSnapshotProvider
{
    public OperationsSnapshot Capture(OperationsLogFilter? logFilter = null)
    {
        ServerRuntimeStateSnapshot state = serverState.Read();
        PlayerSnapshot[] playerSnapshots = players.SnapshotOnlinePlayers().ToArray();
        ModuleOperationsSnapshot[] moduleSnapshots = modules
            .SnapshotModules()
            .Select(static module => module with
            {
                Dependencies = Array.AsReadOnly(module.Dependencies.ToArray())
            })
            .ToArray();
        ModuleDirectoryStatusSnapshot moduleDirectoryStatus =
            moduleDirectory?.Read() ?? ModuleDirectoryStatusSnapshot.Unmonitored;
        string[] warnings = BuildWarnings(state, moduleSnapshots, moduleDirectoryStatus);
        RuntimeLifecycleStage stage = lifecycle.CurrentStage;

        TickRateStatisticsSnapshot tickRate = state.TickRate.SampleCount > 0
            ? state.TickRate
            : new TickRateStatisticsSnapshot(
                state.TicksPerSecond,
                state.TicksPerSecond,
                state.TicksPerSecond,
                state.SampledAt,
                state.TicksPerSecond,
                state.SampledAt,
                state.TicksPerSecond > 0 ? 1 : 0);
        var dashboard = new ServerDashboardSnapshot(
            serverName,
            version,
            startedAt,
            lifecycle.Elapsed,
            stage,
            state.WorldName,
            state.IsWorldReady,
            stage >= RuntimeLifecycleStage.NetworkReady,
            playerSnapshots.Length,
            state.MaximumPlayers,
            state.ListenerPort,
            state.TicksPerSecond,
            Array.AsReadOnly(warnings))
        {
            TickRate = tickRate
        };

        return new OperationsSnapshot(
            dashboard,
            Array.AsReadOnly(playerSnapshots),
            Array.AsReadOnly(moduleSnapshots),
            logs.Query(logFilter ?? new OperationsLogFilter()),
            runtimeMetrics.Read(),
            networkMetrics.Read())
        {
            WorldLoad = worldLoadMetrics?.Read(),
            ModuleDirectory = moduleDirectoryStatus
        };
    }

    private static string[] BuildWarnings(
        ServerRuntimeStateSnapshot state,
        IReadOnlyList<ModuleOperationsSnapshot> modules,
        ModuleDirectoryStatusSnapshot moduleDirectory)
    {
        var warnings = new List<string>();
        if (DateTimeOffset.UtcNow - state.SampledAt > TimeSpan.FromSeconds(5))
        {
            warnings.Add("Terraria runtime snapshot is stale.");
        }

        if (state.TickRate.SampleCount > 0 && state.TickRate.Current < 15)
        {
            warnings.Add($"Critical tick rate: {state.TickRate.Current:F1} TPS.");
        }

        if (moduleDirectory.IsRestartRequired)
        {
            warnings.Add("ServerPlugins change could not be hot-applied; restart required.");
        }

        if (moduleDirectory.Failure is not null)
        {
            warnings.Add($"ServerPlugins hot-reload issue: {moduleDirectory.Failure}");
        }

        foreach (ModuleOperationsSnapshot module in modules)
        {
            if (module.State is ModuleLoadState.Degraded or ModuleLoadState.Failed)
            {
                warnings.Add($"Module {module.Name} is {module.State}: {module.Failure ?? "no details"}");
            }
        }

        return warnings.ToArray();
    }
}

public sealed class StaticModuleOperationsCatalog : IModuleOperationsCatalog
{
    private readonly IReadOnlyList<ModuleOperationsSnapshot> modules;

    public StaticModuleOperationsCatalog(IEnumerable<ModuleOperationsSnapshot> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = Array.AsReadOnly(modules
            .Select(static module => module with
            {
                Dependencies = Array.AsReadOnly(module.Dependencies.ToArray())
            })
            .ToArray());
    }

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => modules;
}

public sealed class CompositeModuleOperationsCatalog(params IModuleOperationsCatalog[] catalogs) : IModuleOperationsCatalog
{
    private readonly IModuleOperationsCatalog[] catalogs = catalogs ??
        throw new ArgumentNullException(nameof(catalogs));

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => Array.AsReadOnly(catalogs
        .SelectMany(static catalog => catalog.SnapshotModules())
        .ToArray());
}
