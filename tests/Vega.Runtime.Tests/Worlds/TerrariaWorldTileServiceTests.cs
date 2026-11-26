using NUnit.Framework;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;
using ITile = global::Terraria.ITile;
using Tile = global::Terraria.Tile;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class TerrariaWorldTileServiceTests
{
    [Test]
    public void OwnedAreaFactoryAndPublicConstructorDoNotExposeMutableInput()
    {
        SceneTile[] source = [new(1), new(2), new(3), new(4)];
        SceneTileArea created = SceneTileArea.Create(2, 2, source,
            static (destination, input) => input.AsSpan().CopyTo(destination));
        var copied = new SceneTileArea(2, 2, source);
        source[0] = new SceneTile(99);
        Assert.That(created[0, 0].TileType, Is.EqualTo(1));
        Assert.That(created[1, 1].TileType, Is.EqualTo(4));
        Assert.That(copied[0, 0].TileType, Is.EqualTo(1));
        Assert.That(() => ((IList<SceneTile>)created.Tiles)[0] = new SceneTile(99), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void CombinedCaptureRetainsLegacyRowMajorFingerprintAndNativeFields()
    {
        var native = CreateGrid(5, 4);
        var bounds = new SceneBounds(1, 1, 3, 2);
        var (area, fingerprint) = TerrariaWorldTileService.CaptureArea(native, bounds);
        var legacy = new HashCode();
        legacy.Add(bounds.Width);
        legacy.Add(bounds.Height);
        foreach (SceneTile tile in area.Tiles) legacy.Add(tile);
        Assert.That(fingerprint, Is.EqualTo((long)legacy.ToHashCode()));

        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
            {
                ITile original = native[bounds.X + x, bounds.Y + y];
                SceneTile captured = area[x, y];
                Assert.That(captured.TileType, Is.EqualTo(original.type));
                Assert.That(captured.WallType, Is.EqualTo(original.wall));
                Assert.That(captured.FrameX, Is.EqualTo(original.frameX));
                Assert.That(captured.FrameY, Is.EqualTo(original.frameY));
                Assert.That(captured.LiquidAmount, Is.EqualTo(original.liquid));
                Assert.That(captured.LiquidKind, Is.EqualTo(original.liquidType()));
                Assert.That(captured.TileColor, Is.EqualTo(original.color()));
                Assert.That(captured.WallColor, Is.EqualTo(original.wallColor()));
                Assert.That(captured.Slope, Is.EqualTo(original.slope()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.Active), Is.EqualTo(original.active()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.WireRed), Is.EqualTo(original.wire()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.FullbrightBlock), Is.EqualTo(original.fullbrightBlock()));
            }
    }

    [Test]
    [NonParallelizable]
    public async Task CapturedAreaIsDetachedAndRevisionsStillGuardReplacement()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        int oldMode = global::Terraria.Main.netMode;
        var native = CreateGrid(4, 4);
        try
        {
            global::Terraria.Main.tile = native;
            global::Terraria.Main.maxTilesX = 4;
            global::Terraria.Main.maxTilesY = 4;
            global::Terraria.Main.netMode = 0;
            var tiles = new TerrariaWorldTileService(new InlineGameThread(), WorldId.Primary);
            var bounds = new SceneBounds(1, 1, 2, 2);
            var original = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            var unchanged = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            Assert.That(unchanged.Revision, Is.EqualTo(original.Revision));
            ushort firstType = original.Tiles[0, 0].TileType;
            native[1, 1].type = (ushort)(firstType + 1);
            var changed = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            Assert.That(changed.Revision, Is.EqualTo(original.Revision + 1));
            Assert.That(original.Tiles[0, 0].TileType, Is.EqualTo(firstType));

            SceneTileArea replacement = SceneTileArea.Fill(2, 2, new SceneTile(1, Flags: SceneTileFlags.Active));
            var conflict = await tiles.ReplaceAsync(new WorldTileMutationRequest(WorldId.Primary, bounds,
                original.Revision, replacement, "test"));
            Assert.That(conflict.Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict));
            Assert.That(conflict.Before!.Revision, Is.EqualTo(changed.Revision));
            Assert.That(native[1, 1].type, Is.EqualTo(firstType + 1));

            var applied = await tiles.ReplaceAsync(new WorldTileMutationRequest(WorldId.Primary, bounds,
                changed.Revision, replacement, "test"));
            Assert.That(applied.Status, Is.EqualTo(WorldTileMutationStatus.Applied));
            Assert.That(applied.Before!.Revision, Is.EqualTo(changed.Revision));
            Assert.That(applied.After!.Revision, Is.EqualTo(changed.Revision + 1));
            Assert.That(applied.After.Tiles.Tiles, Is.EqualTo(replacement.Tiles));
            Assert.That(original.Tiles[0, 0].TileType, Is.EqualTo(firstType));
        }
        finally
        {
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.netMode = oldMode;
        }
    }

    private static global::TerrariaRuntime.DefaultCollection<ITile> CreateGrid(int width, int height)
    {
        var native = new global::TerrariaRuntime.DefaultCollection<ITile>(width, height);
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                var tile = new Tile { type = (ushort)(2 + x + y * width), wall = 1,
                    frameX = (short)(18 * x), frameY = (short)(18 * y), liquid = (byte)(x + y) };
                tile.active((x + y) % 2 == 0);
                tile.wire(x % 2 == 0);
                tile.fullbrightBlock(y % 2 == 0);
                tile.liquidType(y % 4);
                tile.color((byte)x);
                tile.wallColor((byte)y);
                tile.slope((byte)(x % 4));
                native[x, y] = tile;
            }
        return native;
    }

    private sealed class InlineGameThread : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => step();
    }
}
