namespace Vega.Contracts.Worlds;

public sealed record WorldEnvironmentSnapshot(
    WorldId WorldId,
    int WidthTiles,
    int HeightTiles,
    int SpawnTileX,
    int SpawnTileY,
    bool IsDaytime,
    double TimeTicks,
    int MoonPhase,
    bool BloodMoon,
    bool Eclipse,
    bool Raining,
    float RainIntensity,
    float WindSpeed,
    bool Hardmode,
    DateTimeOffset CapturedAt);

public sealed record WorldEnvironmentPatch(
    bool? IsDaytime = null,
    double? TimeTicks = null,
    int? MoonPhase = null,
    bool? BloodMoon = null,
    bool? Eclipse = null,
    bool? Raining = null,
    float? RainIntensity = null,
    float? WindSpeed = null);

public sealed record WorldEnvironmentMutationRequest(
    WorldId WorldId,
    WorldEnvironmentPatch Patch,
    string Actor);

public enum WorldEnvironmentMutationStatus
{
    Applied,
    Invalid,
    DispatcherUnavailable,
    Cancelled,
    Failed
}

public sealed record WorldEnvironmentMutationResult(
    WorldEnvironmentMutationStatus Status,
    WorldEnvironmentSnapshot? Snapshot = null,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == WorldEnvironmentMutationStatus.Applied;
}

public interface IWorldEnvironmentService
{
    ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    ValueTask<WorldEnvironmentMutationResult> PatchAsync(
        WorldEnvironmentMutationRequest request,
        CancellationToken cancellationToken = default);
}
