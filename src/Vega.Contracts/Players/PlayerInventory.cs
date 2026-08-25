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
    DateTimeOffset CapturedAt);

public readonly record struct InventorySlotReplacement(
    int Slot,
    int ItemType,
    int Stack,
    byte Prefix = 0,
    bool Favorited = false);

public readonly record struct InventoryGrantRequest(
    int ItemType,
    int Stack,
    byte Prefix = 0);

public enum InventoryMutationStatus
{
    Applied,
    PlayerNotFound,
    RevisionConflict,
    InvalidItem,
    InventoryFull,
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

    ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerId playerId,
        long expectedRevision,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Atomic item-delivery capability layered on top of optimistic inventory snapshots.
/// Implementations select and merge slots on the authoritative game thread.
/// </summary>
public interface IPlayerInventoryGrantService : IPlayerInventoryService
{
    ValueTask<InventoryMutationResult> GrantAsync(
        PlayerId playerId,
        long expectedRevision,
        InventoryGrantRequest request,
        CancellationToken cancellationToken = default);
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
}
