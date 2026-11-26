using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkWorldContextTests
{
    [SetUp]
    public void ResetRuntime() => VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);

    [TearDown]
    public void ClearRuntime() => VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);

    [Test]
    public async Task PluginVirtualChestOwnsRevisionedContentsAndProviderLifetime()
    {
        var world = new WorldId("plugin-chests");
        var scenes = new SceneRuntime();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, null));
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, opened);
        var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(160, 160), false, 0)
        { WorldId = world, SessionId = opened.SessionId };
        var scope = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.Chests", Path.GetTempPath());
        try
        {
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsVirtualChests, Is.True);
            var provider = fake.CreatePersonal("display", new(10, 10, 4, 4),
                SceneTileArea.Fill(4, 4, new SceneTile(21, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(opened)]);
            using var chest = provider.CreateVirtualChest(0, 0, "Rewards", [new(0, 1, 5, 0)]);
            Assert.That(chest.Definition.Revision, Is.EqualTo(1));
            Assert.That(chest.TryReplace(1, "Rewards", [new(0, 1, 10, 0)]), Is.True);
            Assert.That(chest.TryReplace(1, "stale", []), Is.False);
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(10));
            Assert.Throws<ArgumentException>(() => provider.CreateVirtualChest(1, 1, "overlap", []));
            Assert.That(scenes.TryRequestVirtualChestOpen(player, 10, 10), Is.True);
            Assert.That(chest.TryTakeOpenRequest(out var request), Is.True);
            Assert.That(request!.Value.Chest.Definition.Id, Is.EqualTo(chest.Id));
            Assert.That(request.Value.Chest.Definition.Revision, Is.EqualTo(2));
            provider.Move(5, 0);
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 15, 10, out _), Is.True);
            using var second = provider.CreateVirtualChest(2, 2, "Other", []);
            chest.Dispose();
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 15, 10, out _), Is.False);
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(opened), 17, 12, out _), Is.True);
            Assert.Throws<ObjectDisposedException>(() => _ = chest.Definition);
            await scope.DisposeAsync();
            Assert.That(scenes.HasActiveOverlays, Is.False);
            Assert.Throws<ObjectDisposedException>(() => _ = second.Definition);
        }
        finally { await scope.DisposeAsync(); }
    }

    [Test]
    public async Task FakeProviderUsesScopedLeasesAndExactSessionMembershipAcrossReloads()
    {
        var world = new WorldId("sandbox-fake");
        var scenes = new SceneRuntime();
        var visibility = new SceneVisibilityRuntime();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, null, visibility));
        var first = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(1), "first", DateTimeOffset.UtcNow);
        var second = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(2), "second", DateTimeOffset.UtcNow);
        scenes.OpenSession(world, first); scenes.OpenSession(world, second);
        visibility.OpenSession(world, first); visibility.OpenSession(world, second);
        var a = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.A", Path.GetTempPath());
        var b = new HotModuleRegistrationScope(new NullCommandRegistry(), new NullLogger(), "Plugin.B", Path.GetTempPath());
        try
        {
            IPluginFakeProvider ownerA = ((IVegaPluginContext)a).World.FakeProvider;
            IPluginFakeProvider ownerB = ((IVegaPluginContext)b).World.FakeProvider;
            var bounds = new SceneBounds(10, 10, 2, 2);
            var stone = SceneTileArea.Fill(2, 2, new SceneTile(1, Flags: SceneTileFlags.Active));
            var air = SceneTileArea.Fill(2, 2, SceneTile.Air);
            var global = ownerA.CreateGlobal("same-name", bounds, stone);
            var competing = ownerB.CreateGlobal("same-name", bounds, air);
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 10, 10, out var tile, out _), Is.True);
            Assert.That(tile, Is.EqualTo(SceneTile.Air));
            global.SetTop();
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 10, 10, out tile, out _), Is.True);
            Assert.That(tile.TileType, Is.EqualTo(1));
            var personal = ownerA.CreatePersonal("private", new SceneBounds(20, 20, 2, 2), stone,
                [SceneSessionObserver.From(first), SceneSessionObserver.From(second)], fillOutsideWithAir: true, layer: 10);
            Assert.That(scenes.HasPersonalAirMask(world, first.PlayerId), Is.True);
            personal.SetFillOutsideWithAir(false);
            Assert.That(scenes.HasPersonalAirMask(world, first.PlayerId), Is.False);
            personal.SetFillOutsideWithAir(true);
            personal.Move(1, 0);
            personal.Draw();
            Assert.That(scenes.TryResolveTile(world, first.PlayerId, 21, 20, out _, out _), Is.True);
            var layerA = ownerA.CreateVisibilityLayer("arena", [SceneSessionObserver.From(first)]);
            var layerB = ownerB.CreateVisibilityLayer("arena", [SceneSessionObserver.From(second)]);
            layerA.AssignNpc(7);
            layerB.AssignNpc(7);
            Assert.That(layerA.RemoveNpc(7), Is.False);
            Assert.That(visibility.CanObserve(world, first.PlayerId, new(SceneEntityKind.Npc, 7)), Is.False);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(first)), Is.True);

            var oldClosed = new PlayerSessionClosed(first.SessionId, first.PlayerId, first.PlayerName, DateTimeOffset.UtcNow);
            scenes.CloseSession(world, oldClosed); visibility.CloseSession(world, oldClosed);
            var replacement = first with { SessionId = PlayerSessionId.New() };
            scenes.OpenSession(world, replacement); visibility.OpenSession(world, replacement);
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            Assert.That(scenes.HasPersonalAirMask(world, second.PlayerId), Is.True);
            personal.Move(1, 0); // old requested session cannot be re-added by a later update
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.False);
            Assert.That(scenes.TryResolveTile(world, second.PlayerId, 22, 20, out _, out _), Is.True);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(first)), Is.False);
            Assert.That(layerA.AssignPlayer(SceneSessionObserver.From(replacement)), Is.True);
            Assert.That(layerA.RemovePlayer(SceneSessionObserver.From(first)), Is.False);
            personal.SetObservers([SceneSessionObserver.From(replacement)]);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.True);
            // A delayed duplicate notification for the old generation must not clear the new one.
            scenes.CloseSession(world, oldClosed); visibility.CloseSession(world, oldClosed);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.True);
            personal.SetEnabled(false);
            Assert.That(scenes.TryResolveTile(world, replacement.PlayerId, 22, 20, out _, out _), Is.False);
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            personal.SetEnabled(true);
            for (int i = 0; i < 100; i++) ownerA.CreateGlobal("temporary", bounds, air).Dispose();
            Assert.That(scenes.SnapshotScenes(), Has.Count.EqualTo(3));

            await a.DisposeAsync();
            Assert.That(scenes.HasPersonalAirMask(world, replacement.PlayerId), Is.False);
            Assert.That(scenes.SnapshotScenes().Single().Owner, Is.EqualTo("Plugin.B"));
            Assert.That(visibility.SnapshotLayers().Single().Owner, Is.EqualTo("Plugin.B"));
            Assert.That(visibility.CanObserve(world, first.PlayerId, new(SceneEntityKind.Npc, 7)), Is.False);
            Assert.Throws<ObjectDisposedException>(() => personal.Move(1, 0));
            Assert.Throws<ObjectDisposedException>(() => ownerA.CreateGlobal("late", bounds, air));
            Assert.Throws<ObjectDisposedException>(() => ((IVegaPluginContext)a).World.Scenes.Create("late-legacy"));
            Assert.That(scenes.SnapshotScenes(), Has.Count.EqualTo(1));
            competing.Dispose(); competing.Dispose();
            Assert.That(scenes.HasActiveOverlays, Is.False);
        }
        finally { await a.DisposeAsync(); await b.DisposeAsync(); }
        Assert.That(visibility.SnapshotLayers(), Is.Empty);
    }

    [Test]
    public async Task WorldAndInventoryFacadesUseTypedRuntimeCapabilitiesAndTrackSceneLifetime()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-world-tests", Guid.NewGuid().ToString("N"));
        var environment = new RecordingEnvironment();
        var tiles = new RecordingTiles();
        var chests = new RecordingChests();
        var npcs = new RecordingNpcs();
        var scenes = new RecordingScenes();
        var inventory = new RecordingInventory();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(
            new WorldId("sandbox-a"),
            environment,
            tiles,
            chests,
            npcs,
            scenes,
            inventory));

        var scope = new HotModuleRegistrationScope(
            new NullCommandRegistry(),
            new NullLogger(),
            "Vega.Test.World",
            dataDirectory);
        IVegaPluginContext plugin = scope;

        WorldEnvironmentSnapshot? environmentSnapshot = await plugin.World.Environment.CaptureAsync();
        WorldTileAreaSnapshot? tileSnapshot = await plugin.World.Tiles.CaptureAsync(new SceneBounds(10, 20, 2, 2));
        ChestSnapshot? chestSnapshot = await plugin.World.Chests.CaptureAsync(30, 40);
        IReadOnlyList<NpcSnapshot> npcSnapshot = await plugin.World.Npcs.SnapshotAsync();
        PlayerInventorySnapshot? inventorySnapshot = await plugin.Players.Inventory.CaptureAsync(new PlayerId(3));
        ISceneLease lease = plugin.World.Scenes.Create("test-scene");

        Assert.Multiple(() =>
        {
            Assert.That(plugin.World.Current, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(environmentSnapshot?.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(tileSnapshot?.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(chestSnapshot?.Identity.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(npcSnapshot.Single().Handle.WorldId, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(inventorySnapshot?.PlayerId, Is.EqualTo(new PlayerId(3)));
            Assert.That(scenes.LastOwner, Is.EqualTo("Vega.Test.World"));
            Assert.That(scenes.LastWorld, Is.EqualTo(new WorldId("sandbox-a")));
            Assert.That(((RecordingSceneLease)lease).Disposed, Is.False);
        });

        WorldEnvironmentMutationResult environmentMutation = await plugin.World.Environment.PatchAsync(
            new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 1200));
        WorldTileMutationResult tileMutation = await plugin.World.Tiles.ReplaceAsync(
            tileSnapshot!,
            tileSnapshot!.Tiles);
        ChestMutationResult chestMutation = await plugin.World.Chests.ReplaceAsync(
            chestSnapshot!,
            chestSnapshot!.Items,
            "renamed");
        NpcOperationResult npcMutation = await plugin.World.Npcs.TeleportAsync(
            npcSnapshot.Single().Handle,
            new PlayerPosition(100, 200));
        InventoryMutationResult inventoryMutation = await plugin.Players.Inventory.ReplaceSlotAsync(
            inventorySnapshot!,
            new InventorySlotReplacement(0, 1, 1));

        Assert.Multiple(() =>
        {
            Assert.That(environmentMutation.IsSuccess, Is.True);
            Assert.That(tileMutation.IsSuccess, Is.True);
            Assert.That(chestMutation.IsSuccess, Is.True);
            Assert.That(npcMutation.IsSuccess, Is.True);
            Assert.That(inventoryMutation.IsSuccess, Is.True);
            Assert.That(environment.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(tiles.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(chests.LastActor, Is.EqualTo("Vega.Test.World"));
            Assert.That(npcs.LastActor, Is.EqualTo("Vega.Test.World"));
        });

        await scope.DisposeAsync();
        Assert.That(((RecordingSceneLease)lease).Disposed, Is.True);
        if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
    }

    private sealed class RecordingEnvironment : IWorldEnvironmentService
    {
        public string? LastActor { get; private set; }

        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldEnvironmentSnapshot?>(new WorldEnvironmentSnapshot(
                worldId, 8400, 2400, 100, 200, true, 1000, 0, false, false,
                false, 0, 0, false, DateTimeOffset.UtcNow));

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new WorldEnvironmentMutationResult(
                WorldEnvironmentMutationStatus.Applied,
                new WorldEnvironmentSnapshot(
                    request.WorldId, 8400, 2400, 100, 200,
                    request.Patch.IsDaytime ?? true,
                    request.Patch.TimeTicks ?? 1000,
                    0, false, false, false, 0, 0, false, DateTimeOffset.UtcNow)));
        }
    }

    private sealed class RecordingTiles : IWorldTileService
    {
        public string? LastActor { get; private set; }

        public ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
            WorldId worldId,
            SceneBounds bounds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<WorldTileAreaSnapshot?>(new WorldTileAreaSnapshot(
                worldId,
                bounds,
                7,
                new SceneTileArea(bounds.Width, bounds.Height,
                    Enumerable.Repeat(SceneTile.Air, bounds.Width * bounds.Height).ToArray()),
                DateTimeOffset.UtcNow));

        public ValueTask<WorldTileMutationResult> ReplaceAsync(
            WorldTileMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new WorldTileMutationResult(WorldTileMutationStatus.Applied));
        }
    }

    private sealed class RecordingChests : IChestService
    {
        public string? LastActor { get; private set; }

        public ValueTask<ChestSnapshot?> CaptureAsync(
            ChestIdentity identity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ChestSnapshot?>(new ChestSnapshot(
                identity,
                "test",
                4,
                [new ChestItemSnapshot(0, 1, 1, 0)],
                DateTimeOffset.UtcNow));

        public ValueTask<ChestMutationResult> ReplaceAsync(
            ChestMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new ChestMutationResult(ChestMutationStatus.Applied));
        }
    }

    private sealed class RecordingNpcs : IWorldNpcService
    {
        public string? LastActor { get; private set; }

        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([
                Npc(worldId)
            ]);

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<NpcSnapshot?>(Npc(handle.WorldId));

        public ValueTask<NpcOperationResult> SpawnAsync(
            NpcSpawnRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.WorldId)));
        }

        public ValueTask<NpcOperationResult> TeleportAsync(
            NpcTeleportRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.Expected.WorldId)));
        }

        public ValueTask<NpcOperationResult> DespawnAsync(
            NpcDespawnRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActor = request.Actor;
            return ValueTask.FromResult(new NpcOperationResult(NpcOperationStatus.Applied, Npc(request.Expected.WorldId)));
        }

        private static NpcSnapshot Npc(WorldId worldId) => new(
            new NpcHandle(worldId, 5, 1, 9),
            1,
            "Blue Slime",
            new PlayerPosition(10, 20),
            new WorldVelocity(0, 0),
            25,
            25,
            false,
            false,
            false,
            0,
            DateTimeOffset.UtcNow);
    }

    private sealed class RecordingScenes : ISceneRuntime
    {
        public bool HasActiveOverlays => false;
        public string? LastOwner { get; private set; }
        public WorldId LastWorld { get; private set; }

        public ISceneLease CreateScene(WorldId worldId, string name, string owner)
        {
            LastWorld = worldId;
            LastOwner = owner;
            return new RecordingSceneLease();
        }

        public IReadOnlyList<SceneSnapshot> SnapshotScenes() => [];
        public void ReportFailure(SceneId sceneId, string? failure) { }

        public bool TryResolveTile(
            WorldId worldId,
            PlayerId observer,
            int tileX,
            int tileY,
            out SceneTile tile,
            out OverlayId overlayId)
        {
            tile = default;
            overlayId = default;
            return false;
        }
    }

    private sealed class RecordingSceneLease : ISceneLease
    {
        public SceneId Id { get; } = SceneId.New();
        public bool Disposed { get; private set; }
        public OverlayId AddOverlay(SceneOverlayDefinition definition) => OverlayId.New();
        public bool UpdateOverlay(OverlayId overlayId, SceneOverlayDefinition definition) => true;
        public bool RemoveOverlay(OverlayId overlayId) => true;
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingInventory : IPlayerInventoryService
    {
        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId playerId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlayerInventorySnapshot?>(new PlayerInventorySnapshot(
                playerId,
                "player",
                3,
                [new InventoryItemSnapshot(0, 1, 1, 0, false, "Stone")],
                DateTimeOffset.UtcNow));

        public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
            PlayerId playerId,
            long expectedRevision,
            InventorySlotReplacement replacement,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new InventoryMutationResult(InventoryMutationStatus.Applied));
    }

    private sealed class NullCommandRegistry : ICommandRegistry
    {
        public IDisposable Register(ICommandHandler handler) => new NullRegistration();
    }

    private sealed class NullRegistration : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class NullLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null) { }
    }
}
