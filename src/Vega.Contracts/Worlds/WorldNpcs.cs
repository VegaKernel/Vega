using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public readonly record struct NpcHandle(
    WorldId WorldId,
    int Slot,
    int Type,
    long Revision);

public readonly record struct WorldVelocity(float X, float Y);

public sealed record NpcSnapshot(
    NpcHandle Handle,
    int NetId,
    string Name,
    PlayerPosition Position,
    WorldVelocity Velocity,
    int Life,
    int LifeMax,
    bool Friendly,
    bool TownNpc,
    bool Boss,
    int TargetPlayerSlot,
    DateTimeOffset CapturedAt);

public sealed record NpcSpawnRequest(
    WorldId WorldId,
    int Type,
    PlayerPosition Position,
    string Actor,
    int? TargetPlayerSlot = null);

public sealed record NpcTeleportRequest(
    NpcHandle Expected,
    PlayerPosition Position,
    string Actor);

public sealed record NpcDespawnRequest(
    NpcHandle Expected,
    string Actor);

public enum NpcOperationStatus
{
    Applied,
    NotFound,
    RevisionConflict,
    Invalid,
    DispatcherUnavailable,
    Cancelled,
    Failed
}

public sealed record NpcOperationResult(
    NpcOperationStatus Status,
    NpcSnapshot? Snapshot = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == NpcOperationStatus.Applied;
}

/// <summary>
/// Immutable NPC inspection and guarded mutation. Implementations own Terraria state and game-thread dispatch.
/// </summary>
public interface IWorldNpcService
{
    ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<NpcSnapshot?> CaptureAsync(
        NpcHandle handle,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> SpawnAsync(
        NpcSpawnRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> TeleportAsync(
        NpcTeleportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> DespawnAsync(
        NpcDespawnRequest request,
        CancellationToken cancellationToken = default);
}
