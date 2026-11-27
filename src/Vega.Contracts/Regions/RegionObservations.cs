using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Regions;

public enum RegionChangeKind { Created, Updated, Renamed, AccessChanged, Deleted }

/// <summary>A durable local mutation, after the repository transaction committed. Snapshots are detached.</summary>
public readonly record struct RegionChangedEvent(WorldId WorldId, RegionChangeKind Kind,
    RegionSnapshot? Before, RegionSnapshot? After);

/// <summary>Host-only source of committed local repository mutations. No callback may alter the result.</summary>
public interface IRegionChangeObservations
{
    event Action<RegionChangedEvent>? Changed;
}

/// <summary>Player membership in a region, sampled from the authoritative world position.
/// All containing regions (including overlaps) are observed, not only the top protection region.</summary>
public readonly record struct RegionMembership(WorldId WorldId, PlayerId PlayerId,
    PlayerSessionId SessionId, int TileX, int TileY, RegionSnapshot Region);

public readonly record struct RegionEnteredEvent(RegionMembership Membership);
public readonly record struct RegionLeftEvent(RegionMembership Membership);
