using Vega.Contracts.Scenes;

namespace Vega.Contracts.Worlds;

public static class WorldTileOperationLimits
{
    public const int MaximumTiles = 1_048_576;
}

public sealed record WorldTileAreaSnapshot(
    WorldId WorldId,
    SceneBounds Bounds,
    long Revision,
    SceneTileArea Tiles,
    DateTimeOffset CapturedAt)
{
    /// <summary>Process-local loaded-world identity; never reuse an edit receipt after a world reload.</summary>
    public Guid? WorldInstanceId { get; init; }
}

public sealed record WorldTileMutationRequest(
    WorldId WorldId,
    SceneBounds Bounds,
    long ExpectedRevision,
    SceneTileArea Tiles,
    string Actor)
{
    /// <summary>Exact original tile contents for compare-and-apply, in addition to the observed revision.</summary>
    public SceneTileArea? ExpectedTiles { get; init; }
    public Guid? ExpectedWorldInstanceId { get; init; }
}

public enum WorldTileMutationStatus
{
    Applied,
    NotFound,
    RevisionConflict,
    Invalid,
    Cancelled,
    Failed
}

public sealed record WorldTileMutationResult(
    WorldTileMutationStatus Status,
    WorldTileAreaSnapshot? Before = null,
    WorldTileAreaSnapshot? After = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == WorldTileMutationStatus.Applied;
}

/// <summary>
/// Captures and atomically replaces bounded real-world tile areas without exposing Terraria mutable state.
/// Implementations validate current state and apply mutations on the authoritative world thread.
/// </summary>
public interface IWorldTileService
{
    ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
        WorldId worldId,
        SceneBounds bounds,
        CancellationToken cancellationToken = default);

    ValueTask<WorldTileMutationResult> ReplaceAsync(
        WorldTileMutationRequest request,
        CancellationToken cancellationToken = default);
}
