using Microsoft.Xna.Framework;
using Terraria.ID;
using Main = Terraria.Main;
using WorldGen = Terraria.WorldGen;

namespace Vega.Terraria.Worlds;

internal static class NativeCaveTileCount
{
    internal static void Visit(object? sender, HookEvents.Terraria.WorldGen.nextCountEventArgs args)
    {
        if (!args.ContinueExecution) return;
        args.ContinueExecution = false;
        if (WorldGen.numTileCount >= WorldGen.maxTileCount) return;

        var pending = new Stack<Point>();
        pending.Push(new(args.x, args.y));
        while (WorldGen.numTileCount < WorldGen.maxTileCount && pending.TryPop(out Point point))
        {
            int x = point.X, y = point.Y;
            if (x <= 1 || y <= 1 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1)
            {
                WorldGen.numTileCount = WorldGen.maxTileCount;
                break;
            }
            if (WorldGen.CountedTiles.ContainsKey(point)) continue;
            var tile = Main.tile[x, y];
            if (tile.wall == WallID.LivingWoodUnsafe || (tile.shimmer() && tile.liquid > 0) ||
                (!args.jungle && tile.wall != 0))
            {
                WorldGen.numTileCount = WorldGen.maxTileCount;
                break;
            }
            if (!args.jungle && tile.lava() && tile.liquid > 0)
            {
                WorldGen.lavaCount++;
                if (!args.lavaOk)
                {
                    WorldGen.numTileCount = WorldGen.maxTileCount;
                    break;
                }
            }
            // Vanilla counts solid neighbours on every visit; only open cells become visited.
            if (tile.active())
            {
                switch (tile.type)
                {
                    case TileID.MushroomGrass: WorldGen.shroomCount++; break;
                    case TileID.Stone: WorldGen.rockCount++; break;
                    case TileID.SnowBlock or TileID.IceBlock: WorldGen.iceCount++; break;
                    case TileID.Sand or TileID.Sandstone or TileID.HardenedSand: WorldGen.sandCount++; break;
                }
            }
            if (WorldGen.SolidTile(x, y)) continue;
            WorldGen.CountedTiles.Add(point, true);
            WorldGen.numTileCount++;
            // Reverse push order preserves vanilla's left, right, up, down DFS and its early cutoff.
            pending.Push(new(x, y + 1));
            pending.Push(new(x, y - 1));
            pending.Push(new(x + 1, y));
            pending.Push(new(x - 1, y));
        }
    }
}
