using NUnit.Framework;
using Vega.Terraria.Worlds;
using Tile = global::Terraria.Tile;
using ITile = global::Terraria.ITile;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class PagedNativeTileCollectionTests
{
    [Test]
    public void EmptyReadsAndNativeNullClearsDoNotAllocatePagesButRetainBounds()
    {
        var tiles = new PagedNativeTileCollection(130, 129);
        Assert.Multiple(() =>
        {
            Assert.That(tiles.Width, Is.EqualTo(130));
            Assert.That(tiles.Height, Is.EqualTo(129));
            Assert.That(tiles[129, 128], Is.Null);
            Assert.That(() => _ = tiles[-1, 0], Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => _ = tiles[130, 0], Throws.TypeOf<IndexOutOfRangeException>());
            Assert.That(() => tiles[0, 129] = null!, Throws.TypeOf<IndexOutOfRangeException>());
        });
        tiles[129, 128] = null!;
        tiles[0, 0] = null!;
        Assert.That(tiles.AllocatedPageCount, Is.Zero);
        var last = new Tile();
        tiles[129, 128] = last;
        Assert.That(tiles[129, 128], Is.SameAs(last));
        Assert.That(tiles[128, 128], Is.Null);
        Assert.That(tiles.AllocatedPageCount, Is.EqualTo(1));
    }

    [Test]
    public void ReferenceAssignmentRetainsAliasesAcrossPagesAndDetachedTiles()
    {
        var tiles = new PagedNativeTileCollection(130, 130);
        ITile original = new Tile { type = 1, wall = 2, frameX = 3 };
        tiles[0, 0] = original;
        tiles[64, 64] = original;
        Assert.That(tiles[0, 0], Is.SameAs(tiles[64, 64]));
        tiles[64, 64].type = 42;
        Assert.That(tiles[0, 0].type, Is.EqualTo(42));

        ITile replacement = new Tile { type = 8 };
        tiles[0, 0] = replacement;
        original.type = 12;
        Assert.That(tiles[0, 0], Is.SameAs(replacement));
        Assert.That(tiles[0, 0].type, Is.EqualTo(8));
        Assert.That(tiles[64, 64].type, Is.EqualTo(12));
        tiles[64, 64] = null!;
        Assert.That(tiles[64, 64], Is.Null);
        Assert.That(original.type, Is.EqualTo(12));
    }

    [Test]
    public void NativeCopyCloneAndClearRemainNativeAcrossActiveWorldSizeChanges()
    {
        var tiles = new PagedNativeTileCollection(130, 130);
        ITile original = new Tile { type = 2, wall = 3, frameX = 18, frameY = 36, liquid = 17 };
        original.active(true);
        original.wire(true);
        original.shimmer(true);
        tiles[100, 100] = original;
        var clone = (ITile)original.Clone();
        var copy = new Tile();
        copy.CopyFrom(original);
        tiles[2, 2] = clone;
        Assert.That(clone, Is.Not.SameAs(original));
        Assert.That(copy.isTheSameAs(original), Is.True);

        // Native clearWorld nulls the previous active extent; the factory dimensions remain fixed.
        tiles[100, 100] = null!;
        tiles[2, 2].ClearEverything();
        Assert.That(original.active(), Is.True);
        Assert.That(original.shimmer(), Is.True);
        Assert.That(tiles[100, 100], Is.Null);
        Assert.That(tiles[2, 2], Is.SameAs(clone));
        Assert.That(clone.active(), Is.False);
        Assert.That(tiles.Width, Is.EqualTo(130));
        Assert.That(tiles.Height, Is.EqualTo(130));

        // Subsequent growth uses the same coordinates and retains normal assignment semantics.
        tiles[100, 100] = copy;
        Assert.That(tiles[100, 100], Is.SameAs(copy));
    }

    [Test]
    public void ParallelFirstWritesIntoOnePageDoNotLoseReferenceAssignments()
    {
        var tiles = new PagedNativeTileCollection(64, 64);
        ITile[] columns = Enumerable.Range(0, 64).Select(x => (ITile)new Tile { type = (ushort)x }).ToArray();
        Parallel.For(0, 64, x =>
        {
            for (int y = 0; y < 64; y++) tiles[x, y] = columns[x];
        });
        Assert.That(tiles.AllocatedPageCount, Is.EqualTo(1));
        for (int x = 0; x < 64; x++)
            for (int y = 0; y < 64; y++)
                Assert.That(tiles[x, y], Is.SameAs(columns[x]));
    }

    [Test]
    public void ExplicitOptionIsRemovedFromNativeArgumentsAndDefaultsOff()
    {
        var defaults = NativeTileStorageOptions.Parse(["-port", "7777"]);
        Assert.That(defaults.Enabled, Is.False);
        var enabled = NativeTileStorageOptions.Parse(["-port", "7777", "--vega-paged-tiles=on"]);
        Assert.That(enabled.Enabled, Is.True);
        Assert.That(enabled.ServerArguments, Is.EqualTo(new[] { "-port", "7777" }));
        Assert.That(NativeTileStorageOptions.Parse(["--vega-paged-tiles", "off"]).Enabled, Is.False);
        Assert.That(() => NativeTileStorageOptions.Parse(["--vega-paged-tiles"]), Throws.TypeOf<ArgumentException>());
    }
}
