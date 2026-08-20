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
    IWorldLoadMetricsReader? worldLoadMetrics = null) : IOperationsSnapshotProvider
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
        string[] warnings = BuildWarnings(state, moduleSnapshots);
        RuntimeLifecycleStage stage = lifecycle.CurrentStage;

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
            Array.AsReadOnly(warnings));

        return new OperationsSnapshot(
            dashboard,
            Array.AsReadOnly(playerSnapshots),
            Array.AsReadOnly(moduleSnapshots),
            logs.Query(logFilter ?? new OperationsLogFilter()),
            runtimeMetrics.Read(),
            networkMetrics.Read())
        {
            WorldLoad = worldLoadMetrics?.Read()
        };
    }

    private static string[] BuildWarnings(
        ServerRuntimeStateSnapshot state,
        IReadOnlyList<ModuleOperationsSnapshot> modules)
    {
        var warnings = new List<string>();
        if (DateTimeOffset.UtcNow - state.SampledAt > TimeSpan.FromSeconds(5))
        {
            warnings.Add("Terraria runtime snapshot is stale.");
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
