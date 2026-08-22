using Vega.Contracts.Players;

namespace Vega.Contracts.Worlds;

public readonly record struct WorldTilePosition(int X, int Y);

public interface IWorldInteractionState
{
    bool TryGetActiveChest(PlayerId playerId, out int chestId);

    bool TryGetChestPosition(int chestId, out WorldTilePosition position);

    bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position);
}
