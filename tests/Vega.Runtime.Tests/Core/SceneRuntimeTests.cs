using NUnit.Framework;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Core.Scenes;
using Vega.Modules.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneRuntimeTests
{
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

    private static byte[] Serialize(TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
