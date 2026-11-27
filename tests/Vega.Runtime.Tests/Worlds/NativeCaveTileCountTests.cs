using Microsoft.Xna.Framework;
using NUnit.Framework;
using Terraria;
using Terraria.ID;
using Vega.Terraria.Worlds;
using WG = Terraria.WorldGen;

namespace Vega.Runtime.Tests.Worlds;

[NonParallelizable]
public sealed class NativeCaveTileCountTests
{
    [Test]
    public void IterativeWalkMatchesNativeOrderAndCountersAndHandlesADeepCave()
    {
        var oldGrid = Main.tile;
        var oldSize = (Main.maxTilesX, Main.maxTilesY);
        var oldSolid = Main.tileSolid;
        var oldVisited = WG.CountedTiles;
        var oldCounts = (WG.maxTileCount, WG.numTileCount, WG.shroomCount, WG.lavaCount, WG.rockCount, WG.iceCount, WG.sandCount);
        try
        {
            const int width = 8200;
            (Main.maxTilesX, Main.maxTilesY) = (width, 7);
            Main.tile = new PagedNativeTileCollection(width, 7);
            Main.tileSolid = (bool[])oldSolid.Clone();
            WG.CountedTiles = new();
            ushort[] types = [TileID.Stone, TileID.MushroomGrass, TileID.SnowBlock, TileID.HardenedSand];
            foreach (var type in types) Main.tileSolid[type] = true;
            for (int x = 1; x < width - 1; x++)
            for (int y = 2; y <= 4; y++)
            {
                var tile = new Tile { type = types[x % types.Length] };
                tile.active(y != 3 || x <= 2 || x >= width - 3);
                Main.tile[x, y] = tile;
            }
            var lava = Main.tile[20, 3];
            lava.lava(true);
            lava.liquid = 100;
            Main.tile[28, 3].wall = 1;
            Main.tile[60, 3].wall = WallID.LivingWoodUnsafe;
            foreach (var (jungle, lavaOk, limit) in new[] { (false, false, 80), (false, true, 80), (true, false, 80), (true, true, 12) })
            {
                WG.maxTileCount = limit;
                var expected = Count(jungle, lavaOk);
                Point[] visited = WG.CountedTiles.Keys.ToArray();
                HookEvents.Terraria.WorldGen.nextCount += NativeCaveTileCount.Visit;
                try
                {
                    Assert.That(Count(jungle, lavaOk), Is.EqualTo(expected));
                    Assert.That(WG.CountedTiles.Keys, Is.EqualTo(visited));
                }
                finally { HookEvents.Terraria.WorldGen.nextCount -= NativeCaveTileCount.Visit; }
            }
            Main.tile[60, 3].wall = 0;
            WG.maxTileCount = width;
            HookEvents.Terraria.WorldGen.nextCount += NativeCaveTileCount.Visit;
            try { Assert.That(WG.countTiles(3, 3, true, true), Is.EqualTo(width - 6)); }
            finally { HookEvents.Terraria.WorldGen.nextCount -= NativeCaveTileCount.Visit; }
        }
        finally
        {
            Main.tile = oldGrid;
            (Main.maxTilesX, Main.maxTilesY) = oldSize;
            Main.tileSolid = oldSolid;
            WG.CountedTiles = oldVisited;
            (WG.maxTileCount, WG.numTileCount, WG.shroomCount, WG.lavaCount, WG.rockCount, WG.iceCount, WG.sandCount) = oldCounts;
        }
    }

    private static (int, int, int, int, int, int) Count(bool jungle, bool lavaOk) =>
        (WG.countTiles(3, 3, jungle, lavaOk), WG.shroomCount, WG.lavaCount, WG.rockCount, WG.iceCount, WG.sandCount);
}
