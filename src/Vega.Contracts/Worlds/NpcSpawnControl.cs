namespace Vega.Contracts.Worlds;

public sealed record NpcSpawnLimitSnapshot(
    WorldId WorldId,
    int MaximumSpawns,
    int Capacity);

public sealed record NpcSpawnLimitMutationResult(
    NpcOperationStatus Status,
    NpcSpawnLimitSnapshot? Snapshot = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == NpcOperationStatus.Applied;
}

public interface IWorldNpcSpawnControl
{
    ValueTask<NpcSpawnLimitSnapshot?> CaptureAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<NpcSpawnLimitMutationResult> SetMaximumSpawnsAsync(
        WorldId worldId,
        int maximumSpawns,
        string actor,
        CancellationToken cancellationToken = default);
}
