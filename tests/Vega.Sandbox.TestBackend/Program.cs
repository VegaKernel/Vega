using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Sandboxes;

SandboxBackendLaunchOptions launch = SandboxBackendLaunchOptions.Parse(args);
using var reporter = new SandboxBackendReporter(
    launch,
    new ReadyLifecycle(),
    new ReadyServer(),
    new EmptyPlayers());
while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase)) break;
}

internal sealed class ReadyLifecycle : IRuntimeLifecycle
{
    public RuntimeLifecycleStage CurrentStage => RuntimeLifecycleStage.FullyWarmed;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(1);
    public event Action<RuntimeLifecycleTransition>? Transitioned { add { } remove { } }
}

internal sealed class ReadyServer : IServerRuntimeStateReader
{
    public ServerRuntimeStateSnapshot Read() => new("test", 8, 7780, true, 60, DateTimeOffset.UtcNow);
}

internal sealed class EmptyPlayers : IPlayerDirectory
{
    public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Array.Empty<PlayerSnapshot>();
    public bool TryGet(PlayerId playerId, out PlayerSnapshot? player) { player = null; return false; }
    public bool TryFindExact(string name, out PlayerSnapshot? player) { player = null; return false; }
}
