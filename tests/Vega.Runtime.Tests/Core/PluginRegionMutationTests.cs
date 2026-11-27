using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.Persistence.Sqlite;
using Vega.PluginSdk;
using Vega.Regions;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Core;

[TestFixture, NonParallelizable]
public sealed class PluginRegionMutationTests
{
    [Test]
    public async Task PluginRegionsReusesDurableAclAndRejectsForeignWorldAndRecreatedIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-plugin-regions-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "vega.db")));
            var world = new WorldId("local-world");
            var foreign = new WorldId("foreign-world");
            var dispatcher = new Dispatcher();
            var events = new WorldGameplayEventHub();
            var players = new Players();
            var logger = new Logger();
            using var observations = new RegionObservations(database.Regions, players, events, world, dispatcher, logger);
            VegaPluginRuntime.Configure(new(world, new EnvironmentFixture(), null, null, null, null, null, Regions: database.Regions));
            await using var scope = new HotModuleRegistrationScope(new Registry(), logger, "Plugin.RegionFixture", root,
                gameThread: dispatcher, worldEvents: events, activityWorldId: world);
            var plugin = (IVegaPluginContext)scope;
            var regions = plugin.World.Regions;
            var changes = new List<RegionChangedEvent>();
            plugin.WorldEvents.OnRegionChanged(changes.Add);

            Assert.That((await regions.CreateAsync("outside", 90, 90, 20, 20)).Code, Is.EqualTo("region.invalid-bounds"));
            var original = (await regions.CreateAsync("home", 5, 5, 10, 10, 1, "alice")).After!;
            Assert.That(original.WorldId, Is.EqualTo(world));
            Assert.That(changes, Is.Empty, "Callbacks must not run inside the repository mutation.");
            dispatcher.Drain();
            Assert.That(changes.Single().Kind, Is.EqualTo(RegionChangeKind.Created));
            Assert.That((await regions.UpdateAsync(original with { WorldId = foreign }, new(Priority: 3))).Code, Is.EqualTo("region.wrong-world"));
            Assert.That((await regions.UpdateAsync(original with { X = 0 }, new(Width: 100))).Code, Is.EqualTo("region.invalid-bounds"));
            var access = (await regions.SetAccessAsync(original, RegionAccessKind.Member, "bob", true)).After!;
            Assert.That((await regions.DeleteAsync(original)).Code, Is.EqualTo("region.revision-conflict"));
            var flag = (await regions.UpdateAsync(access, new(Flag: "build", FlagValue: false))).After!;
            var renamed = (await regions.RenameAsync(flag, "new-home")).After!;
            Assert.That(renamed.Id, Is.EqualTo(original.Id));
            Assert.That(renamed.Members.Contains("BOB"), Is.True);
            Assert.That(renamed.Flags["build"], Is.False);

            // Command, REST and other host callers use the same repository and publish the same event.
            Assert.That((await database.Regions.DeleteAsync(world, renamed.Name, renamed.Revision, "Server")).Succeeded, Is.True);
            var recreated = (await regions.CreateAsync("home", 5, 5, 10, 10)).After!;
            Assert.That(recreated.Id, Is.Not.EqualTo(original.Id));
            Assert.That((await regions.DeleteAsync(original)).Code, Is.EqualTo("region.identity-conflict"));
            Assert.That((await database.Regions.CreateAsync(foreign, "other", 1, 1, 3, 3, 0, null, "Server")).Succeeded, Is.True);
            dispatcher.Drain();
            Assert.That(changes.Select(change => change.Kind), Is.EqualTo(new[] { RegionChangeKind.Created,
                RegionChangeKind.AccessChanged, RegionChangeKind.Updated, RegionChangeKind.Renamed,
                RegionChangeKind.Deleted, RegionChangeKind.Created }));
            Assert.That(regions.Snapshot().Single().Id, Is.EqualTo(recreated.Id));
            await scope.DisposeAsync();
            Assert.That((await database.Regions.DeleteAsync(world, recreated.Name, recreated.Revision, "Server")).Succeeded, Is.True);
            dispatcher.Drain();
            Assert.That(changes, Has.Count.EqualTo(6));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task RegionMembershipObservesOverlapsOnlyOnActualTileOrBoundaryChangeAndSessionExit()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-plugin-region-membership-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = await SqliteVegaDatabase.OpenAsync(new(Path.Combine(root, "vega.db")));
            var world = new WorldId("membership");
            var outer = (await database.Regions.CreateAsync(world, "outer", 0, 0, 20, 20, 0, null, "Server")).After!;
            var inner = (await database.Regions.CreateAsync(world, "inner", 4, 4, 4, 4, 1, null, "Server")).After!;
            var events = new WorldGameplayEventHub();
            var dispatcher = new Dispatcher();
            var players = new Players();
            var logger = new Logger();
            using var observations = new RegionObservations(database.Regions, players, events, world, dispatcher, logger);
            await using var scope = new HotModuleRegistrationScope(new Registry(), logger, "Plugin.MembershipFixture", root,
                worldEvents: events, activityWorldId: world);
            var plugin = (IVegaPluginContext)scope;
            var entered = new List<RegionEnteredEvent>();
            var left = new List<RegionLeftEvent>();
            plugin.WorldEvents.OnRegionEntered(entered.Add);
            plugin.WorldEvents.OnRegionLeft(left.Add);
            var session = PlayerSessionId.New();
            var otherSession = PlayerSessionId.New();
            var player = new PlayerSnapshot(new(1), "Player", false, 100, 100, 20, 20, new(5 * 16, 5 * 16), false, 0)
                { SessionId = session, WorldId = world };
            players.Online = [player, player with { Id = new(2), SessionId = otherSession, WorldId = new("other") }];
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(entered.Select(x => x.Membership.Region.Id), Is.EquivalentTo(new[] { outer.Id, inner.Id }));
            Assert.That(entered.All(x => x.Membership.SessionId == session), Is.True);
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(entered, Has.Count.EqualTo(2));

            // ACL revision changes should not repeatedly emit an enter event for the same stable region.
            inner = (await database.Regions.SetAccessAsync(world, inner.Name, RegionAccessKind.Group, "builders", true, inner.Revision, "Server")).After!;
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(entered, Has.Count.EqualTo(2));
            Assert.That(left, Is.Empty);
            Assert.That((await database.Regions.UpdateAsync(world, inner.Name, new(X: 10), inner.Revision, "Server")).Succeeded, Is.True);
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(left.Single().Membership.Region.Id, Is.EqualTo(inner.Id));

            players.Online = [player with { Position = new(12 * 16, 5 * 16) }];
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(entered.Last().Membership.Region.Id, Is.EqualTo(inner.Id));
            players.Online = [];
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(left.Skip(1).Select(x => x.Membership.Region.Id), Is.EquivalentTo(new[] { outer.Id, inner.Id }));
            players.Online = [player with { SessionId = otherSession }];
            events.Publish(new WorldTickCompletedEvent(world, TimeSpan.Zero));
            Assert.That(entered.Last().Membership.SessionId, Is.EqualTo(otherSession));
            await scope.DisposeAsync();
            Assert.That(events.HasSubscribers<RegionEnteredEvent>(), Is.False);
            Assert.That(events.HasSubscribers<RegionLeftEvent>(), Is.False);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task PluginTileUndoAndRedoCompareExactSnapshotsAndRejectAWorldReload()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY, oldMode = global::Terraria.Main.netMode;
        var world = new WorldId("edit");
        var dispatcher = new Dispatcher();
        using var tiles = new TerrariaWorldTileService(dispatcher, world);
        try
        {
            var grid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(4, 4);
            for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) grid[x, y] = new global::Terraria.Tile { type = 1 };
            grid[1, 1].liquidType(2); // Native zero-amount liquid state must round-trip in a receipt.
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.maxTilesX = global::Terraria.Main.maxTilesY = 4;
            global::Terraria.Main.netMode = 0;
            VegaPluginRuntime.Configure(new(world, null, tiles, null, null, null, null));
            await using var scope = new HotModuleRegistrationScope(new Registry(), new Logger(), "Plugin.TileFixture", Path.GetTempPath());
            var api = ((IVegaPluginContext)scope).World.Tiles;
            var bounds = new SceneBounds(1, 1, 2, 2);
            var original = (await api.CaptureAsync(bounds))!;
            var filled = SceneTileArea.Fill(2, 2, new SceneTile(2, Flags: SceneTileFlags.Active));
            var forged = original with { Tiles = SceneTileArea.Fill(2, 2, new SceneTile(99)) };
            Assert.That((await api.ReplaceAsync(forged, filled)).Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict),
                "A matching revision alone is not an exact compare-and-apply proof.");
            Assert.That((await api.CaptureAsync(bounds))!.Tiles.Tiles, Is.EqualTo(original.Tiles.Tiles));

            var edit = await api.ReplaceAsync(original, filled);
            Assert.That(edit.IsSuccess, Is.True);
            var undo = await api.UndoAsync(edit);
            Assert.That(undo.IsSuccess, Is.True);
            Assert.That(undo.After!.Tiles.Tiles, Is.EqualTo(original.Tiles.Tiles));
            Assert.That(grid[1, 1].liquidType(), Is.EqualTo(2));
            var redo = await api.UndoAsync(undo);
            Assert.That(redo.IsSuccess, Is.True);
            Assert.That(redo.After!.Tiles.Tiles, Is.EqualTo(filled.Tiles));

            grid[1, 1].type = 3;
            Assert.That((await api.UndoAsync(redo)).Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict));
            Assert.That(grid[1, 1].type, Is.EqualTo(3));
            Assert.That(grid[2, 2].type, Is.EqualTo(2));

            var latest = (await api.CaptureAsync(bounds))!;
            var secondEdit = await api.ReplaceAsync(latest, filled);
            Assert.That(secondEdit.IsSuccess, Is.True);
            var replacementGrid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(4, 4);
            // An identical grid image loaded as a new native world invalidates the previous receipt.
            for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++) replacementGrid[x, y] = grid[x, y];
            global::Terraria.Main.tile = replacementGrid;
            Assert.That((await api.UndoAsync(secondEdit)).Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict));
            Assert.That((await api.CaptureAsync(bounds))!.WorldInstanceId, Is.Not.EqualTo(secondEdit.After!.WorldInstanceId));
            Assert.That((await api.UndoAsync(new(WorldTileMutationStatus.Failed))).Status, Is.EqualTo(WorldTileMutationStatus.Invalid));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.netMode = oldMode;
        }
    }

    [Test]
    public async Task TileShapeCacheEvictionNeverRecyclesAnOldRevision()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        var world = new WorldId("cache");
        using var service = new TerrariaWorldTileService(new Dispatcher(), world);
        try
        {
            const int width = 65, height = 65;
            var grid = new global::TerrariaRuntime.DefaultCollection<global::Terraria.ITile>(width, height);
            for (int x = 0; x < width; x++) for (int y = 0; y < height; y++) grid[x, y] = new global::Terraria.Tile();
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.maxTilesX = width;
            global::Terraria.Main.maxTilesY = height;
            var first = (await service.CaptureAsync(world, new SceneBounds(0, 0, 1, 1)))!;
            for (int i = 1; i <= TerrariaWorldTileService.MaximumTrackedAreas; i++)
                Assert.That(await service.CaptureAsync(world, new SceneBounds(i % width, i / width, 1, 1)), Is.Not.Null);
            var stale = await service.ReplaceAsync(new(world, first.Bounds, first.Revision, SceneTileArea.Fill(1, 1, new SceneTile(1)), "test")
            { ExpectedTiles = first.Tiles, ExpectedWorldInstanceId = first.WorldInstanceId });
            Assert.That(stale.Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict));
            Assert.That(stale.Before!.Revision, Is.GreaterThan(first.Revision));
        }
        finally
        {
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
        }
    }

    private sealed class EnvironmentFixture : IWorldEnvironmentService
    {
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(WorldId worldId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new(worldId, 100, 100, 5, 5, true, 0, 0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow));
        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(WorldEnvironmentMutationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Players : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> Online { get; set; } = [];
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Online;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = Online.FirstOrDefault(x => x.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = Online.FirstOrDefault(x => x.Name == name)) is not null;
    }
    private sealed class Dispatcher : IGameThreadDispatcher
    {
        private readonly Queue<Action> pending = new();
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { pending.Enqueue(action); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Drain() { while (pending.TryDequeue(out var action)) action(); }
    }
    private sealed class Registry : ICommandRegistry { public IDisposable Register(ICommandHandler handler) => throw new NotSupportedException(); }
    private sealed class Logger : IOperationsLogger { public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { } }
}
