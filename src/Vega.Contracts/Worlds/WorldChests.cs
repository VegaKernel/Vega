using Vega.Contracts.Events;
using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public readonly record struct ChestIdentity(WorldId WorldId, int X, int Y)
{
    public override string ToString() => $"{WorldId}:{X},{Y}";
}

public readonly record struct ChestItemSnapshot(
    int Slot,
    int ItemType,
    int Stack,
    byte Prefix);

public sealed record ChestSnapshot(
    ChestIdentity Identity,
    string Name,
    long Revision,
    IReadOnlyList<ChestItemSnapshot> Items,
    DateTimeOffset CapturedAt);

public sealed record ChestMutationRequest(
    ChestIdentity Identity,
    long ExpectedRevision,
    IReadOnlyList<ChestItemSnapshot> Items,
    string Actor,
    PlayerId? ActorPlayerId = null,
    string? Name = null);

public sealed record ChestChanging(ChestMutationRequest Request, ChestSnapshot Current);

public sealed record ChestChanged(ChestMutationRequest Request, ChestSnapshot Before, ChestSnapshot After);

public enum ChestMutationStatus
{
    Applied,
    NotFound,
    RevisionConflict,
    Rejected,
    Invalid,
    Cancelled,
    Failed
}

public sealed record ChestMutationResult(
    ChestMutationStatus Status,
    ChestSnapshot? Before = null,
    ChestSnapshot? After = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == ChestMutationStatus.Applied;
}

public interface IChestBackend
{
    ValueTask<ChestSnapshot?> CaptureAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<ChestMutationResult> ReplaceAsync(
        ChestMutationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IChestService
{
    ValueTask<ChestSnapshot?> CaptureAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<ChestMutationResult> ReplaceAsync(
        ChestMutationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IChestInteractionPolicy
{
    ValueTask ObserveAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default);
}
