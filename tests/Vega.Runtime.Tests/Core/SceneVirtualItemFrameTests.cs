using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using TerraRuntime.Schematics;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Core.Scenes;
using Vega.Modules.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneVirtualItemFrameTests
{
    private static SceneTileArea FrameTiles() => new(2, 2,
    [
        new(395, FrameX: 0, FrameY: 0, Flags: SceneTileFlags.Active),
        new(395, FrameX: 18, FrameY: 0, Flags: SceneTileFlags.Active),
        new(395, FrameX: 0, FrameY: 18, Flags: SceneTileFlags.Active),
        new(395, FrameX: 18, FrameY: 18, Flags: SceneTileFlags.Active)
    ]);

    [Test]
    public void NativeFrameMetadataRequiresVisibleCompleteFootprintAndRedrawsOnRevisionChange()
    {
        var world = new WorldId("frames");
        var sink = new RecordingSink();
        var runtime = new SceneRuntime(sink);
        var session = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "Viewer", DateTimeOffset.UtcNow);
        runtime.OpenSession(world, session);
        var observer = SceneSessionObserver.From(session);
        var frame = new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(), 0, 0, 9, 1, 0, 1);
        var definition = new SceneOverlayDefinition(default, "frame", new(100,100,2,2), 1,
            SceneObserverScope.PersonalSessions([observer]), FrameTiles()) { VirtualItemFrames = [frame] };
        using var scene = runtime.CreateScene(world, "frames", "test");
        Assert.Throws<ArgumentException>(() => scene.AddOverlay(definition with { Tiles = SceneTileArea.Fill(2,2,SceneTile.Air) }));
        Assert.Throws<ArgumentException>(() => scene.AddOverlay(definition with { VirtualSigns = [new(VirtualSignId.New(),0,0,"overlap",1)] }));
        var overlay = scene.AddOverlay(definition);
        var corner = new SceneBounds(101,101,1,1);
        Assert.That(runtime.HasVirtualItemFrames(world), Is.True);
        Assert.That(runtime.SnapshotVisibleVirtualItemFrames(world, observer, corner).Single().Definition, Is.SameAs(frame));
        Assert.That(runtime.SnapshotVisibleVirtualItemFrames(world, observer with { Session = PlayerSessionId.New() }, corner), Is.Empty);
        using (var cover = runtime.CreateScene(world, "cover", "test"))
        {
            cover.AddOverlay(new(default,"corner",corner,2,SceneObserverScope.Global,SceneTileArea.Fill(1,1,SceneTile.Air)));
            Assert.That(runtime.SnapshotVisibleVirtualItemFrames(world, observer, corner), Is.Empty);
        }
        sink.Requests.Clear();
        definition = definition with { VirtualItemFrames = [new(frame.Id,0,0,8,1,0,2)] };
        Assert.That(scene.UpdateOverlay(overlay,definition), Is.True);
        Assert.That(sink.Requests, Has.Count.EqualTo(1));
        Assert.That(runtime.SnapshotVisibleVirtualItemFrames(world,observer,corner).Single().Definition.Revision, Is.EqualTo(2));

        // Ordinary inbound frame placement is blocked on phantom tiles, not on the canonical world.
        var packet = new PlaceItemFrame { X = 100, Y = 100, ItemId = 9, Stack = 1 };
        using var bytes = new MemoryStream();
        packet.ToStream(bytes, false);
        var view = PacketView.FromPayload(PacketTypes.PlaceItemFrame, bytes.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, PacketDirection.Inbound, new ConnectionId(observer.Player.Slot), observer.Player, DateTimeOffset.UtcNow);
        Assert.That(new SceneInteractionAuthorityPolicy(runtime, world).Process(in context),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        scene.Dispose();
        Assert.That(runtime.HasVirtualItemFrames(world), Is.False);
    }

    [Test]
    public void SchematicSceneImportRetainsItemFrameAsNoncanonicalMetadata()
    {
        var document = new SchematicDocument
        {
            ContentVersion = 1, Width = 2, Height = 2,
            Tiles = FrameTiles().Tiles.Select(tile => new SchematicTile(tile.TileType,0,tile.FrameX,tile.FrameY,SchematicTileFlags.Active,0,0,0,0,0)).ToArray(),
            TileEntities = [new SchematicItemFrameTileEntity(0,0,new(9,1))]
        };
        var projected = SchematicSceneProjection.Create(document,"frame",100,100,SceneObserverScope.Global);
        Assert.That(projected.VirtualItemFrames.Single().ItemType, Is.EqualTo(9));
        var runtime = new SceneRuntime();
        using var scene = runtime.CreateScene(WorldId.Primary,"import","test");
        Assert.DoesNotThrow(() => scene.AddOverlay(projected));
    }

    private sealed class RecordingSink : ISceneRenderSink
    {
        public List<SceneRenderRequest> Requests { get; } = [];
        public void Invalidate(SceneRenderRequest request) => Requests.Add(request);
    }
}
