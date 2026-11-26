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
    DateTimeOffset CapturedAt)
{
    /// <summary>Persistent identity embedded in the loaded world file; distinct from the runtime context.</summary>
    public Guid? FileIdentity { get; init; }
    public string? Name { get; init; }
    public int? Seed { get; init; }
    public int? GameMode { get; init; }
    public WorldTilePosition? Dungeon { get; init; }
    public int? DefaultSpawnRate { get; init; }
    public int? DefaultMaxSpawns { get; init; }
    public bool ForceHalloween { get; init; }
    public bool ForceChristmas { get; init; }
    public IReadOnlyList<NativeWorldEvent> NativeEvents { get; init; } = [];
}

public sealed record WorldEnvironmentPatch(
    bool? IsDaytime = null,
    double? TimeTicks = null,
    int? MoonPhase = null,
    bool? BloodMoon = null,
    bool? Eclipse = null,
    bool? Raining = null,
    float? RainIntensity = null,
    float? WindSpeed = null,
    WorldTilePosition? Spawn = null,
    WorldTilePosition? Dungeon = null,
    int? DefaultSpawnRate = null,
    int? DefaultMaxSpawns = null,
    int? GameMode = null,
    bool? ForceHalloween = null,
    bool? ForceChristmas = null,
    bool ClearAnglerCompletion = false,
    NativeWorldEventChange? NativeEvent = null,
    bool StartHardmode = false);

public enum NativeWorldEvent { SlimeRain, Sandstorm, PumpkinMoon, FrostMoon, GoblinArmy, SnowLegion, PirateInvasion, MartianMadness }
public sealed record NativeWorldEventChange(NativeWorldEvent Event, bool Enabled);

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
