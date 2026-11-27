using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Terraria.ID;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class TerrariaWorldEnvironmentMutationTests
{
    [Test]
    public async Task InvalidCompoundPatchNeverAppliesEarlierValidClockChanges()
    {
        double originalTime = global::Terraria.Main.time;
        bool originalDay = global::Terraria.Main.dayTime;
        var service = new TerrariaWorldEnvironmentService(new GameThread(), WorldId.Primary);
        try
        {
            var result = await service.PatchAsync(new(WorldId.Primary,
                new WorldEnvironmentPatch(IsDaytime: !originalDay, TimeTicks: 100, WindSpeed: 2f), "fixture"));
            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(WorldEnvironmentMutationStatus.Invalid));
                Assert.That(result.ErrorCode, Is.EqualTo("world.environment.invalid_wind_speed"));
                Assert.That(global::Terraria.Main.time, Is.EqualTo(originalTime));
                Assert.That(global::Terraria.Main.dayTime, Is.EqualTo(originalDay));
            });
        }
        finally
        {
            global::Terraria.Main.time = originalTime;
            global::Terraria.Main.dayTime = originalDay;
        }
    }

    [TestCase(WorldGrowKind.Cactus)]
    [TestCase(WorldGrowKind.Herb)]
    [TestCase(WorldGrowKind.Topaz)]
    public async Task IncompatibleGrowthLeavesNativeGroundUnchanged(WorldGrowKind kind)
    {
        var originalGrid = global::Terraria.Main.tile;
        int originalWidth = global::Terraria.Main.maxTilesX, originalHeight = global::Terraria.Main.maxTilesY;
        bool originalMenu = global::Terraria.Main.gameMenu;
        var grid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(100, 100);
        var ground = new global::Terraria.Tile { type = TileID.Grass };
        ground.active(true);
        grid[50, 50] = ground;
        var dispatcher = new GameThread();
        using var players = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), WorldId.Primary);
        var service = new TerrariaWorldNativeActions(dispatcher, WorldId.Primary, players);
        try
        {
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.maxTilesX = global::Terraria.Main.maxTilesY = 100;
            global::Terraria.Main.gameMenu = false;
            var result = await service.GrowAsync(new(WorldId.Primary, kind, Coordinates: new(50, 50)));
            Assert.That(result.Status, Is.EqualTo(WorldNativeActionStatus.Invalid));
            Assert.That(result.ErrorCode, Is.EqualTo("grow.incompatible_ground"));
            Assert.That(grid[50, 50].active(), Is.True);
            Assert.That(grid[50, 50].type, Is.EqualTo(TileID.Grass));
        }
        finally
        {
            global::Terraria.Main.tile = originalGrid;
            global::Terraria.Main.maxTilesX = originalWidth; global::Terraria.Main.maxTilesY = originalHeight;
            global::Terraria.Main.gameMenu = originalMenu;
        }
    }

    private sealed class GameThread : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) => throw new NotSupportedException();
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }
}
