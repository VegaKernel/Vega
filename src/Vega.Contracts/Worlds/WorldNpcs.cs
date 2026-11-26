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
    int? TargetPlayerSlot = null)
{
    /// <summary>When set, the native adapter must resolve the same live session on the game thread.</summary>
    public PlayerSnapshot? TargetPlayer { get; init; }
    public bool Boss { get; init; }
}

public sealed record NpcButcherRequest(WorldId WorldId, int? Type, bool IncludeBosses, string Actor);

public sealed record NpcRenameRequest(WorldId WorldId, int Type, string Name, string Actor);

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

public sealed record NpcBatchOperationResult(NpcOperationStatus Status, int Affected = 0, string? ErrorCode = null)
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

    ValueTask<NpcBatchOperationResult> ButcherAsync(NpcButcherRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new NpcBatchOperationResult(NpcOperationStatus.Invalid, ErrorCode: "npc.unsupported"));

    ValueTask<NpcBatchOperationResult> RenameAsync(NpcRenameRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new NpcBatchOperationResult(NpcOperationStatus.Invalid, ErrorCode: "npc.unsupported"));
}

public sealed record NpcTypeDefinition(int Type, string Name, bool Boss = false);

public interface IWorldNpcCatalog
{
    ValueTask<IReadOnlyList<NpcTypeDefinition>> FindTypesAsync(string query, CancellationToken cancellationToken = default);
}
