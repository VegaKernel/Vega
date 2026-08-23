using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxWorldContextCatalog(
    ISandboxControlService sandboxes,
    IServerRuntimeStateReader primary,
    IPlayerDirectory players) : IWorldContextOperationsCatalog
{
    public bool CanMovePlayers => sandboxes.Snapshot().Any(static status =>
        status.State == SandboxLifecycleState.Ready);

    public IReadOnlyList<WorldContextSnapshot> SnapshotContexts()
    {
        ServerRuntimeStateSnapshot server = primary.Read();
        var contexts = new List<WorldContextSnapshot>
        {
            new(
                WorldId.Primary,
                server.WorldName ?? "Primary world",
                WorldContextKind.Primary,
                server.IsWorldReady ? WorldContextSimulationState.Running : WorldContextSimulationState.Stopped,
                players.SnapshotOnlinePlayers().Select(static player => player.Id).ToArray(),
                0,
                "primary",
                TimeSpan.Zero)
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        contexts.AddRange(sandboxes.Snapshot().Select(status =>
        {
            TimeSpan remaining = status.Descriptor.IdleTimeToLive is TimeSpan ttl &&
                                 status.ReadyAt is DateTimeOffset readyAt
                ? TimeSpan.FromTicks(Math.Max(0, (readyAt + ttl - now).Ticks))
                : TimeSpan.Zero;
            return new WorldContextSnapshot(
                status.Descriptor.WorldId,
                status.Descriptor.DisplayName,
                WorldContextKind.Sandbox,
                status.State is SandboxLifecycleState.Starting or SandboxLifecycleState.Ready or SandboxLifecycleState.Degraded
                    ? WorldContextSimulationState.Running
                    : WorldContextSimulationState.Stopped,
                Array.Empty<PlayerId>(),
                (int)Math.Min(int.MaxValue, status.Descriptor.Revision),
                $"{status.Descriptor.Persistence}/{status.Descriptor.SscMode}",
                remaining)
            {
                Sandbox = status
            };
        }));
        return contexts;
    }
}
