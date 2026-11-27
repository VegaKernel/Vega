using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerBoundary : ISceneGestureToolState
{
    public bool IsHoldingGestureTool(PlayerSnapshot player) => dispatcher.IsGameThread && TryGetSessionPlayer(player, out var live) &&
        !live.dead && (uint)live.selectedItem < (uint)live.inventory.Length &&
        live.inventory[live.selectedItem] is { stack: > 0, type: ItemID.WireKite };
}
