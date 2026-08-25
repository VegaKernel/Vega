using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
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
        Directory.Delete(Path.GetDirectoryName(dataDirectory)!, recursive: true);
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
