using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

public readonly record struct InventoryItemSnapshot(
    int Slot,
    int ItemType,
    int Stack,
    byte Prefix,
    bool Favorited,
    string Name);

public sealed record PlayerInventorySnapshot(
    PlayerId PlayerId,
    string PlayerName,
    long Revision,
    IReadOnlyList<InventoryItemSnapshot> Items,
    DateTimeOffset CapturedAt)
{
    public PlayerSessionId? SessionId { get; init; }
    public WorldId WorldId { get; init; } = WorldId.Primary;
}

public readonly record struct InventorySlotReplacement(
    int Slot,
    int ItemType,
    int Stack,
    byte Prefix = 0,
    bool Favorited = false);

public enum InventoryMutationStatus
{
    Applied,
    PlayerNotFound,
    RevisionConflict,
    InvalidItem,
    DispatcherUnavailable,
    Cancelled,
    Failed
}

public readonly record struct InventoryMutationResult(
    InventoryMutationStatus Status,
    PlayerInventorySnapshot? Snapshot = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == InventoryMutationStatus.Applied;
}

public interface IPlayerInventoryService
{
    ValueTask<PlayerInventorySnapshot?> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerInventorySnapshot?> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        CaptureAsync(player.Id, cancellationToken);

    ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerId playerId,
        long expectedRevision,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default);

    ValueTask<InventoryMutationResult> ReplaceSlotAsync(PlayerSnapshot player, long expectedRevision, InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default) => ReplaceSlotAsync(player.Id, expectedRevision, replacement, cancellationToken);
}

public enum PlayerInspectionExposure
{
    Redacted,
    Sensitive
}

public sealed record PlayerInspectionSnapshot(
    PlayerSnapshot Player,
    PlayerInventorySnapshot Inventory);

public interface IPlayerInspectionService
{
    ValueTask<PlayerInspectionSnapshot?> CaptureAsync(
        PlayerId playerId,
        PlayerInspectionExposure exposure = PlayerInspectionExposure.Redacted,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerInspectionSnapshot?> CaptureAsync(PlayerSnapshot player, PlayerInspectionExposure exposure = PlayerInspectionExposure.Redacted,
        CancellationToken cancellationToken = default) => CaptureAsync(player.Id, exposure, cancellationToken);
}
