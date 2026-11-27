using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public enum WorldGrowKind
{
    Basic, Boreal, Mahogany, Sakura, Willow, Shadewood, Ebonwood, Pearlwood,
    Palm, HallowPalm, CrimsonPalm, CorruptPalm, Topaz, Amethyst, Sapphire, Emerald, Ruby, Diamond, Amber, Mushroom, Cactus, Herb
}

public enum WorldNativeActionStatus { Applied, AlreadyActive, Invalid, Unavailable, Cancelled, Failed }

public sealed record WorldNativeActionResult(WorldNativeActionStatus Status, string? ErrorCode = null,
    bool? Crimson = null, WorldTilePosition? Position = null)
{
    public bool IsSuccess => Status is WorldNativeActionStatus.Applied or WorldNativeActionStatus.AlreadyActive;
}

public sealed record WorldGrowRequest(WorldId WorldId, WorldGrowKind Kind, PlayerSnapshot? Player = null,
    WorldTilePosition? Coordinates = null);

public interface IWorldNativeMaintenanceOperations
{
    ValueTask<WorldNativeActionResult> SwitchEvilAsync(WorldId worldId, bool? crimson, CancellationToken cancellationToken = default);
    ValueTask<WorldNativeActionResult> SettleAsync(WorldId worldId, CancellationToken cancellationToken = default);
    ValueTask<WorldNativeActionResult> GrowAsync(WorldGrowRequest request, CancellationToken cancellationToken = default);
}
