using Vega.Contracts.Scenes;

namespace Vega.Contracts.Worlds;

public enum WorldSchematicCaptureStatus { Captured, Busy, Unavailable, Invalid }

/// <summary>Detached encoded schematic data owned by the caller.</summary>
public sealed record WorldSchematicCaptureResult(WorldSchematicCaptureStatus Status,
    ReadOnlyMemory<byte> Data = default, string? Error = null);

public enum WorldSchematicPlacementStatus { Applied, Busy, Unavailable, Invalid, Failed }
public sealed record WorldSchematicPlacementResult(WorldSchematicPlacementStatus Status, int NpcsPlaced = 0, string? Error = null);

public interface IWorldSchematicService
{
    ValueTask<WorldSchematicCaptureResult> CaptureAsync(WorldId worldId, SceneBounds bounds,
        CancellationToken cancellationToken = default);
    ValueTask<WorldSchematicPlacementResult> PlaceAsync(WorldId worldId, ReadOnlyMemory<byte> data, int x, int y,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(new WorldSchematicPlacementResult(WorldSchematicPlacementStatus.Unavailable));
}
