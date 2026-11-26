using Vega.Contracts.Players;

namespace Vega.Contracts.Items;

public enum RestrictedContentKind { Item, Projectile, Tile }

public interface IContentRestrictionPolicy
{
    bool IsAllowed(PlayerId player, RestrictedContentKind kind, int type);
}

/// <summary>Live inventory selection lookup for packet policy on the authoritative game thread.</summary>
public interface IPlayerItemSelection
{
    bool TryGetItemType(PlayerId player, int slot, out int type);
}
