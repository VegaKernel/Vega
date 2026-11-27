using Vega.Contracts.Worlds;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerBoundary : IWorldSignState
{
    public bool TryGetSignPosition(int signId, out WorldTilePosition position)
    {
        if ((uint)signId < (uint)global::Terraria.Main.sign.Length && global::Terraria.Main.sign[signId] is { } sign)
        {
            position = new(sign.x, sign.y);
            return true;
        }
        position = default;
        return false;
    }
}
