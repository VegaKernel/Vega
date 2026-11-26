using NUnit.Framework;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Core.Commands;
using Vega.Core.Scenes;
using Vega.Modules.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneRuntimeTests
{
    [Test]
    public void RegionIntersectionRespectsWorldObserverEnabledAndExclusiveEdges()
    {
        var runtime = new SceneRuntime();
        var observer = new PlayerId(1);
        using var scene = runtime.CreateScene(WorldId.Primary, "region", "test");
        var definition = Overlay("personal", 1, SceneObserverScope.Personal(observer));
        var id = scene.AddOverlay(definition);
        var inside = new SceneBounds(11, 11, 10, 10);
        Assert.That(definition.Bounds.TryIntersect(inside, out var clipped), Is.True);
        Assert.That(clipped, Is.EqualTo(new SceneBounds(11, 11, 1, 1)));
        var adjacent = new SceneBounds(12, 10, 6, 6);
        Assert.That(definition.Bounds.TryIntersect(adjacent, out _), Is.False);
        Assert.That(runtime.HasOverlayInRegion(WorldId.Primary, adjacent), Is.False);
        Assert.That(runtime.HasOverlayInRegion(WorldId.Primary, inside), Is.True);
        Assert.That(runtime.HasOverlayInRegion(WorldId.Primary, inside, observer), Is.True);
        Assert.That(runtime.HasOverlayInRegion(WorldId.Primary, inside, new PlayerId(2)), Is.False);
        Assert.That(runtime.HasOverlayInRegion(new WorldId("other"), inside), Is.False);
        scene.UpdateOverlay(id, definition with { Enabled = false });
        Assert.That(runtime.HasOverlayInRegion(WorldId.Primary, inside), Is.False);
    }

    [Test]
    public void UnchangedBoundsRedrawOnceWithImmutableUnionOfOldAndNewObservers()
    {
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        using var scene = runtime.CreateScene(WorldId.Primary, "frame", "test");
        var first = new PlayerId(1);
        var second = new PlayerId(2);
        var third = new PlayerId(3);
        var definition = Overlay("frame", 1, SceneObserverScope.Personal([first, second]));
        var id = scene.AddOverlay(definition);
        scene.UpdateOverlay(id, definition with { Observers = SceneObserverScope.Personal([second, third]) });
        Assert.That(sink.Invalidations, Has.Count.EqualTo(2));
        Assert.That(sink.Invalidations[0].Observers, Is.EquivalentTo(new[] { first, second }));
        Assert.That(sink.Invalidations[1].Observers, Is.EquivalentTo(new[] { first, second, third }));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, first, 10, 10, out _, out _), Is.False);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, third, 10, 10, out _, out _), Is.True);
        scene.UpdateOverlay(id, definition with { Bounds = new SceneBounds(20, 20, 2, 2) });
        Assert.That(sink.Invalidations, Has.Count.EqualTo(4));
        Assert.That(sink.Invalidations[1].Observers, Is.EquivalentTo(new[] { first, second, third }));
    }
    [Test]
    public void PublishedLookupIsWorldScopedFrozenAndInvalidatedByEveryMutation()
    {
        var runtime = new SceneRuntime();
        var observer = new PlayerId(1);
        var outsider = new PlayerId(2);
        var other = new WorldId("other");
        using ISceneLease primary = runtime.CreateScene(WorldId.Primary, "primary", "test");
        using ISceneLease secondary = runtime.CreateScene(other, "secondary", "test");
        var mutable = new HashSet<PlayerId> { observer };
        var first = Overlay("first", 1, new SceneObserverScope(false, mutable));
        OverlayId id = primary.AddOverlay(first);
        secondary.AddOverlay(Overlay("other", 9, SceneObserverScope.Global));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out _, out _), Is.True);
        mutable.Clear();
        mutable.Add(outsider);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out _, out _), Is.True);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, outsider, 10, 10, out _, out _), Is.False);
        Assert.That(runtime.TryResolveTile(other, outsider, 10, 10, out var otherTile, out _), Is.True);
        Assert.That(otherTile.TileType, Is.EqualTo(9));
        primary.UpdateOverlay(id, first with { Enabled = false });
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out _, out _), Is.False);
        primary.UpdateOverlay(id, first with { Enabled = true, Observers = SceneObserverScope.Personal([observer]), Bounds = new SceneBounds(20, 20, 2, 2) });
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 20, 20, out _, out _), Is.True);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out _, out _), Is.False);
        primary.RemoveOverlay(id);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 20, 20, out _, out _), Is.False);
        secondary.Dispose();
        Assert.That(runtime.HasActiveOverlays, Is.False);
    }

    [Test]
    public void SparseLookupPreservesLayerPrecedenceAcrossBucketBoundariesAndMovement()
    {
        var runtime = new SceneRuntime();
        using var scene = runtime.CreateScene(WorldId.Primary, "buckets", "test");
        var observer = new PlayerId(1);
        var other = new PlayerId(2);
        for (int i = 0; i < 8; i++)
            scene.AddOverlay(Overlay($"background{i}", 1, SceneObserverScope.Global) with { Bounds = new SceneBounds(i * 64, 128, 2, 2) });
        var global = Overlay("global", 3, SceneObserverScope.Global) with { Bounds = new SceneBounds(63, -1, 2, 2) };
        scene.AddOverlay(global);
        var personal = Overlay("personal", 4, SceneObserverScope.Personal(observer)) with { Bounds = global.Bounds };
        var id = scene.AddOverlay(personal);
        foreach (var (x, y) in new[] { (63, -1), (64, -1), (63, 0), (64, 0) })
        {
            Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, x, y, out var tile, out _), Is.True);
            Assert.That(tile.TileType, Is.EqualTo(4));
            Assert.That(runtime.TryResolveTile(WorldId.Primary, other, x, y, out tile, out _), Is.True);
            Assert.That(tile.TileType, Is.EqualTo(3));
        }
        scene.UpdateOverlay(id, personal with { Bounds = new SceneBounds(130, 65, 2, 2) });
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 64, 0, out var old, out _), Is.True);
        Assert.That(old.TileType, Is.EqualTo(3));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 131, 66, out var moved, out _), Is.True);
        Assert.That(moved.TileType, Is.EqualTo(4));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, other, 131, 66, out _, out _), Is.False);
    }

    [Test]
    public void FakeProviderModuleComposesNamedLayersAndCleansPersonalObserversBeforeSlotReuse()
    {
        var runtime = new SceneRuntime();
        var sessions = new TestSessions();
        using var module = new SceneConsumersModule(runtime, new SceneVisibilityRuntime(), sessions);
        module.Start();
        var provider = module.FakeProviders;
        var observer = new PlayerId(1);
        var outsider = new PlayerId(2);
        var bounds = new SceneBounds(10, 10, 2, 2);
        var global = Overlay("global", 1, SceneObserverScope.Global).Tiles;
        var personal = Overlay("personal", 2, SceneObserverScope.Global).Tiles;

        Assert.That(provider.Create("terrain", bounds, global), Is.True);
        Assert.That(provider.Create("preview", bounds, personal, [observer], layer: 10), Is.True);
        Assert.That(provider.Create("PREVIEW", bounds, global), Is.False);
        Assert.That(provider.Snapshot(false), Has.Count.EqualTo(1));
        Assert.That(provider.Snapshot(true), Has.Count.EqualTo(1));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out var tile, out _), Is.True);
        Assert.That(tile.TileType, Is.EqualTo(2));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, outsider, 10, 10, out tile, out _), Is.True);
        Assert.That(tile.TileType, Is.EqualTo(1));
        Assert.That(provider.SetEnabled("preview", true, false), Is.True);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 10, 10, out tile, out _), Is.True);
        Assert.That(tile.TileType, Is.EqualTo(1));
        Assert.That(provider.SetEnabled("preview", true, true), Is.True);
        Assert.That(provider.Move("preview", true, 10, 0), Is.True);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 20, 10, out tile, out _), Is.True);
        Assert.That(tile.TileType, Is.EqualTo(2));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, outsider, 20, 10, out _, out _), Is.False);
        Assert.That(provider.SetLayer("preview", false, 100), Is.False);
        sessions.Close(observer);
        Assert.That(provider.Snapshot(true), Is.Empty);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 20, 10, out _, out _), Is.False);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, outsider, 10, 10, out tile, out _), Is.True);
        Assert.That(tile.TileType, Is.EqualTo(1));
        module.Dispose();
        Assert.That(runtime.HasActiveOverlays, Is.False);
    }

    [Test]
    public async Task FakeProviderCommandsRequireManagementPermissionAndTargetOnlyOnlinePersonalObservers()
    {
        var runtime = new SceneRuntime();
        using var service = new FakeProviderService(runtime);
        var player = new PlayerSnapshot(new PlayerId(3), "Alice", false, 100, 100, 20, 20, new(0, 0), false, 0)
            { SessionId = PlayerSessionId.New() };
        var directory = new PlayerDirectory(player);
        var dispatcher = new CommandDispatcher([
            new FakeProviderCommand(service, directory, new ImmediateGameThread(), false),
            new FakeProviderCommand(service, directory, new ImmediateGameThread(), true)]);
        ValueTask<CommandExecutionResult> Run(string name, bool allowed, params string[] arguments) =>
            dispatcher.DispatchAsync(new CommandInvocation(name, arguments, new Principal(allowed), new Output()));

        Assert.That((await Run("fake", false, "create", "global", "10", "10", "2", "2")).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
        Assert.That((await Run("fake", true, "create", "global", "10", "10", "2", "2", "stone")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await Run("pfake", true, "create", "missing", "Offline", "10", "10", "2", "2")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("pfake", true, "create", "mine", "Alice", "10", "10", "2", "2")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, player.Id, 10, 10, out var privateTile, out _), Is.True);
        Assert.That(privateTile, Is.EqualTo(SceneTile.Air));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, new PlayerId(4), 10, 10, out var publicTile, out _), Is.True);
        Assert.That(publicTile.TileType, Is.EqualTo(1));
        Assert.That((await Run("fake", true, "remove", "mine")).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        Assert.That((await Run("pfake", true, "remove", "mine")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(service.Snapshot(true), Is.Empty);
    }

    [Test]
    public void LaterOverlayWinsStableOrderAndConflictIsObservable()
    {
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        using ISceneLease scene = runtime.CreateScene(WorldId.Primary, "preview", "WorldEdit:test");
        PlayerId observer = new(3);
        SceneObserverScope scope = SceneObserverScope.Personal([observer]);
        scene.AddOverlay(Overlay("first", 1, scope));
        OverlayId winner = scene.AddOverlay(Overlay("second", 2, scope));

        bool found = runtime.TryResolveTile(
            WorldId.Primary,
            observer,
            10,
            10,
            out SceneTile tile,
            out OverlayId selected);
        SceneSnapshot snapshot = runtime.SnapshotScenes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(tile.TileType, Is.EqualTo(2));
            Assert.That(selected, Is.EqualTo(winner));
            Assert.That(snapshot.ConflictCount, Is.EqualTo(1));
            Assert.That(snapshot.Owner, Is.EqualTo("WorldEdit:test"));
            Assert.That(sink.Invalidations, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void LeaseDisposalRemovesEveryOwnedOverlayAndInvalidatesItsArea()
    {
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        ISceneLease scene = runtime.CreateScene(WorldId.Primary, "region", "Region-Vision:test");
        scene.AddOverlay(Overlay("border", 7, SceneObserverScope.Global));

        scene.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.SnapshotScenes(), Is.Empty);
            Assert.That(sink.Invalidations, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void AdapterFailureIsVisibleAndCanBeCleared()
    {
        var runtime = new SceneRuntime();
        using ISceneLease scene = runtime.CreateScene(WorldId.Primary, "preview", "test");

        runtime.ReportFailure(scene.Id, "send failed");
        Assert.That(runtime.SnapshotScenes().Single().Failure, Is.EqualTo("send failed"));

        runtime.ReportFailure(scene.Id, null);
        Assert.That(runtime.SnapshotScenes().Single().Failure, Is.Null);
    }

    [Test]
    public void VisibilityLayerUsesSameDecisionForAssignedEntitiesAndCleansUpWithLease()
    {
        var runtime = new SceneVisibilityRuntime();
        PlayerId member = new(1);
        PlayerId outsider = new(2);
        var entity = new SceneEntity(SceneEntityKind.Npc, 7);
        IVisibilityLayerLease layer = runtime.CreateLayer(
            WorldId.Primary,
            "arena",
            "FakeProviderClustering:arena",
            [member]);
        layer.Assign(entity);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.CanObserve(WorldId.Primary, member, entity), Is.True);
            Assert.That(runtime.CanObserve(WorldId.Primary, outsider, entity), Is.False);
            Assert.That(runtime.SnapshotLayers().Single().Entities, Does.Contain(entity));
        });

        layer.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(runtime.CanObserve(WorldId.Primary, outsider, entity), Is.True);
            Assert.That(runtime.SnapshotLayers(), Is.Empty);
        });
    }

    [Test]
    public void InboundInteractionWithHiddenNpcIsRejectedByTheSharedVisibilityDecision()
    {
        var visibility = new SceneVisibilityRuntime();
        var observer = new PlayerId(4);
        using IVisibilityLayerLease layer = visibility.CreateLayer(
            WorldId.Primary,
            "arena",
            "test",
            [new PlayerId(3)]);
        layer.Assign(new SceneEntity(SceneEntityKind.Npc, 7));
        byte[] payload = Serialize(new NpcStrike { NpcSlot = 7 });
        PacketView packet = PacketView.FromPayload(PacketTypes.NpcStrike, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(observer.Slot),
            observer,
            DateTimeOffset.UtcNow);

        PacketProcessingDecision decision = new SceneEntityInteractionAuthorityPolicy(visibility)
            .Process(in context);

        Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Reject("scene.hidden_entity_interaction")));
    }

    [Test]
    public void WorldEditPreviewReusesItsSceneAndRemovesAnObsoleteMask()
    {
        var runtime = new SceneRuntime();
        using var previews = new WorldEditPreviewService(runtime);
        var observer = new PlayerId(2);
        var tiles = new SceneTileArea(2, 2, Enumerable.Repeat(SceneTile.Air, 4).ToArray());
        previews.Show(observer, "edit", new SceneBounds(10, 10, 2, 2), tiles, tiles);
        SceneId sceneId = runtime.SnapshotScenes().Single().Id;

        bool moved = previews.Move(observer, new SceneBounds(20, 20, 2, 2), tiles);
        SceneSnapshot snapshot = runtime.SnapshotScenes().Single();

        Assert.Multiple(() =>
        {
            Assert.That(moved, Is.True);
            Assert.That(snapshot.Id, Is.EqualTo(sceneId));
            Assert.That(snapshot.Overlays, Has.Count.EqualTo(1));
            Assert.That(snapshot.Overlays[0].Bounds.X, Is.EqualTo(20));
        });
    }

    [Test]
    public void LobbyAnimationReusesOverlayAndHiddenPortalIsAirScopedToObserver()
    {
        var runtime = new SceneRuntime();
        using var visuals = new LobbyVisualService(runtime);
        var observer = new PlayerId(5);
        var bounds = new SceneBounds(30, 40, 2, 2);
        var first = new SceneTileArea(2, 2, Enumerable.Repeat(new SceneTile(1, Flags: SceneTileFlags.Active), 4).ToArray());
        var second = new SceneTileArea(2, 2, Enumerable.Repeat(new SceneTile(2, Flags: SceneTileFlags.Active), 4).ToArray());
        visuals.Show("panel", bounds, first, [observer]);
        SceneId scene = runtime.SnapshotScenes().Single().Id;

        Assert.That(visuals.Update("panel", bounds, second, [observer]), Is.True);
        Assert.That(runtime.SnapshotScenes().Single().Id, Is.EqualTo(scene));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 30, 40, out SceneTile updated, out _), Is.True);
        Assert.That(updated.TileType, Is.EqualTo(2));

        visuals.HidePortal("panel", bounds, [observer]);
        Assert.That(runtime.TryResolveTile(WorldId.Primary, observer, 30, 40, out SceneTile hidden, out _), Is.True);
        Assert.That(hidden, Is.EqualTo(SceneTile.Air));
        Assert.That(runtime.TryResolveTile(WorldId.Primary, new PlayerId(6), 30, 40, out _, out _), Is.False);
    }

    [Test]
    public void LobbyCompositionKeepsFramePortalAndClusterIndependentAndRemovesObserverEverywhere()
    {
        var runtime = new SceneRuntime();
        var visibility = new SceneVisibilityRuntime();
        using var visuals = new LobbyVisualService(runtime);
        using var clustering = new FakeProviderClusteringService(visibility);
        using var composition = new LobbySceneCompositionService(visuals, clustering);
        var first = new PlayerId(1);
        var second = new PlayerId(2);
        var frameBounds = new SceneBounds(10, 10, 2, 2);
        var portalBounds = new SceneBounds(20, 20, 2, 2);
        var frame = new SceneTileArea(2, 2, Enumerable.Repeat(new SceneTile(4, Flags: SceneTileFlags.Active), 4).ToArray());
        var npc = new SceneEntity(SceneEntityKind.Npc, 7);

        composition.ShowFrame("spawn", frameBounds, frame, [first, second]);
        composition.HidePortal("spawn", portalBounds, [first, second]);
        composition.SetCluster("spawn", [first, second], [npc]);

        Assert.That(runtime.SnapshotScenes(), Has.Count.EqualTo(2));
        Assert.That(visibility.CanObserve(WorldId.Primary, first, npc), Is.True);
        composition.RemoveObserver(first);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.TryResolveTile(WorldId.Primary, first, 10, 10, out _, out _), Is.False);
            Assert.That(runtime.TryResolveTile(WorldId.Primary, second, 10, 10, out SceneTile frameTile, out _), Is.True);
            Assert.That(frameTile.TileType, Is.EqualTo(4));
            Assert.That(runtime.TryResolveTile(WorldId.Primary, first, 20, 20, out _, out _), Is.False);
            Assert.That(runtime.TryResolveTile(WorldId.Primary, second, 20, 20, out SceneTile portalTile, out _), Is.True);
            Assert.That(portalTile, Is.EqualTo(SceneTile.Air));
            Assert.That(visibility.CanObserve(WorldId.Primary, first, npc), Is.False);
            Assert.That(visibility.CanObserve(WorldId.Primary, second, npc), Is.True);
        });
    }

    [Test]
    public void ClusteringConsumerAssignsPlayerNpcAndProjectileInOneTransition()
    {
        var visibility = new SceneVisibilityRuntime();
        using var clustering = new FakeProviderClusteringService(visibility);
        var observer = new PlayerId(1);
        SceneEntity[] entities =
        [
            new(SceneEntityKind.Player, 2),
            new(SceneEntityKind.Npc, 10),
            new(SceneEntityKind.Projectile, 20)
        ];

        clustering.CreateOrReplace("lobby", [observer], entities);
        VisibilityLayerSnapshot snapshot = visibility.SnapshotLayers().Single();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Entities, Is.EquivalentTo(entities));
            Assert.That(entities.All(entity => visibility.CanObserve(WorldId.Primary, observer, entity)), Is.True);
            Assert.That(entities.All(entity => !visibility.CanObserve(WorldId.Primary, new PlayerId(3), entity)), Is.True);
        });
    }

    private static SceneOverlayDefinition Overlay(
        string name,
        ushort tileType,
        SceneObserverScope observers) =>
        new(
            default,
            name,
            new SceneBounds(10, 10, 2, 2),
            5,
            observers,
            new SceneTileArea(
                2,
                2,
                Enumerable.Repeat(
                    new SceneTile(tileType, Flags: SceneTileFlags.Active),
                    4).ToArray()));

    private sealed class RecordingSink : ISceneRenderSink
    {
        public List<SceneRenderRequest> Invalidations { get; } = [];

        public void Invalidate(SceneRenderRequest request) => Invalidations.Add(request);
    }

    private sealed class TestSessions : IPlayerSessionLifecycle, IPlayerSessionClosing
    {
        public event Action<PlayerSessionOpened>? Opened { add { } remove { } }
        public event Action<PlayerSessionClosed>? Closed;
        public event Action<PlayerSessionClosed>? Closing;

        public void Close(PlayerId player)
        {
            var closed = new PlayerSessionClosed(PlayerSessionId.New(), player, "test", DateTimeOffset.UtcNow);
            Closing?.Invoke(closed);
            Closed?.Invoke(closed);
        }
    }

    private sealed class PlayerDirectory(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? found) { found = player.Id == id ? player : null; return found is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? found) { found = player.Name == name ? player : null; return found is not null; }
    }

    private sealed class ImmediateGameThread : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => step();
    }

    private sealed class Principal(bool allowed) : ICommandPrincipal
    {
        public string Name => "test";
        public bool IsServer => true;
        public bool HasPermission(string permission) => allowed && permission == "vega.scenes.manage";
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static byte[] Serialize(TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
