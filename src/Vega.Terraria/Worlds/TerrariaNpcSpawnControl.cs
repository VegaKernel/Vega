using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaNpcSpawnControl(
    IGameThreadDispatcher dispatcher,
    WorldId worldId) : IWorldNpcSpawnControl
{
    public ValueTask<NpcSpawnLimitSnapshot?> CaptureAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync<NpcSpawnLimitSnapshot?>(
            "world.npcs.spawn-limit.capture",
            () => requestedWorldId == worldId ? Capture() : null,
            cancellationToken,
            null);

    public ValueTask<NpcSpawnLimitMutationResult> SetMaximumSpawnsAsync(
        WorldId requestedWorldId,
        int maximumSpawns,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return DispatchAsync(
            "world.npcs.spawn-limit.set",
            () => Set(requestedWorldId, maximumSpawns),
            cancellationToken,
            Unavailable(),
            Cancelled());
    }

    public ValueTask<NpcSpawnRateSnapshot?> CaptureSpawnRateAsync(
        WorldId requestedWorldId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync<NpcSpawnRateSnapshot?>(
            "world.npcs.spawn-rate.capture",
            () => requestedWorldId == worldId ? CaptureSpawnRate() : null,
            cancellationToken,
            null);

    public ValueTask<NpcSpawnRateMutationResult> SetSpawnRateAsync(
        WorldId requestedWorldId,
        int spawnRate,
        string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return DispatchAsync(
            "world.npcs.spawn-rate.set",
            () => SetSpawnRate(requestedWorldId, spawnRate),
            cancellationToken,
            SpawnRateUnavailable(),
            SpawnRateCancelled());
    }

    private NpcSpawnLimitSnapshot Capture()
    {
        dispatcher.AssertGameThread();
        return new NpcSpawnLimitSnapshot(
            worldId,
            global::Terraria.NPC.defaultMaxSpawns,
            global::Terraria.Main.maxNPCs);
    }

    private NpcSpawnLimitMutationResult Set(WorldId requestedWorldId, int maximumSpawns)
    {
        dispatcher.AssertGameThread();
        if (requestedWorldId != worldId || maximumSpawns < 0 || maximumSpawns > global::Terraria.Main.maxNPCs)
        {
            return new NpcSpawnLimitMutationResult(
                NpcOperationStatus.Invalid,
                ErrorCode: "npc.spawn_limit_invalid");
        }

        global::Terraria.NPC.defaultMaxSpawns = maximumSpawns;
        return new NpcSpawnLimitMutationResult(NpcOperationStatus.Applied, Capture());
    }

    private NpcSpawnRateSnapshot CaptureSpawnRate()
    {
        dispatcher.AssertGameThread();
        return new NpcSpawnRateSnapshot(worldId, global::Terraria.NPC.defaultSpawnRate);
    }

    private NpcSpawnRateMutationResult SetSpawnRate(WorldId requestedWorldId, int spawnRate)
    {
        dispatcher.AssertGameThread();
        if (requestedWorldId != worldId || spawnRate < 0)
        {
            return new NpcSpawnRateMutationResult(
                NpcOperationStatus.Invalid,
                ErrorCode: "npc.spawn_rate_invalid");
        }

        global::Terraria.NPC.defaultSpawnRate = spawnRate;
        return new NpcSpawnRateMutationResult(NpcOperationStatus.Applied, CaptureSpawnRate());
    }

    private static NpcSpawnLimitMutationResult Unavailable() =>
        new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.dispatcher_unavailable");

    private static NpcSpawnLimitMutationResult Cancelled() =>
        new(NpcOperationStatus.Cancelled, ErrorCode: "npc.operation_cancelled");

    private static NpcSpawnRateMutationResult SpawnRateUnavailable() =>
        new(NpcOperationStatus.DispatcherUnavailable, ErrorCode: "npc.dispatcher_unavailable");

    private static NpcSpawnRateMutationResult SpawnRateCancelled() =>
        new(NpcOperationStatus.Cancelled, ErrorCode: "npc.operation_cancelled");

    private ValueTask<T> DispatchAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken,
        T unavailable,
        T? cancelled = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled!);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                operation,
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action())))
        {
            return ValueTask.FromResult(unavailable);
        }

        return new ValueTask<T>(completion.Task);
    }
}
