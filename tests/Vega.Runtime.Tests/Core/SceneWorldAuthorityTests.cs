using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;
using Vega.Core.Scenes;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SceneWorldAuthorityTests
{
    private static readonly WorldId Arena = new("arena");
    private static readonly PlayerId Viewer = new(4);

    [Test]
    public void OrdinaryVirtualChestOpenIsObservedWithoutPassingToNativeChestMutation()
    {
        var scenes = new SceneRuntime();
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), Viewer, "Viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(Arena, opened);
        var player = new PlayerSnapshot(Viewer, "Viewer", false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { WorldId = Arena, SessionId = opened.SessionId };
        using var scene = scenes.CreateScene(Arena, "virtual chest", "test");
        var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), 0, 0, "display", 1, []);
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default, "container", new(100, 100, 2, 2), 1,
            SceneObserverScope.Global, SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active))) { VirtualChests = [chest] });
        var click = new ChestGetContents { TileX = 100, TileY = 100 };
        Assert.That(Process(new SceneInteractionAuthorityPolicy(scenes, Arena, new Directory(player)), click, PacketTypes.ChestGetContents, Viewer),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(scenes.TryTakeVirtualChestOpen(scene.Id, overlay, chest.Id, out var request), Is.True);
        Assert.That(request!.Value.Observer, Is.EqualTo(SceneSessionObserver.From(opened)));
    }

    [Test]
    public void OrdinaryVirtualSignReadProducesIntentButCanonicalSignsRemainNative()
    {
        var scenes = new SceneRuntime();
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), Viewer, "Viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(Arena, opened);
        var player = new PlayerSnapshot(Viewer, "Viewer", false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { WorldId = Arena, SessionId = opened.SessionId };
        using var scene = scenes.CreateScene(Arena, "virtual sign", "test");
        var sign = new SceneVirtualSignDefinition(VirtualSignId.New(), 0, 0, "Welcome", 1);
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default, "sign", new(100, 100, 2, 2), 1,
            SceneObserverScope.Global, SceneTileArea.Fill(2, 2, new SceneTile(55, Flags: SceneTileFlags.Active))) { VirtualSigns = [sign] });
        var policy = new SceneInteractionAuthorityPolicy(scenes, Arena, new Directory(player));
        Assert.That(Process(policy, new SignRead { X = 50, Y = 50 }, PacketTypes.SignRead, Viewer),
            Is.EqualTo(PacketProcessingDecision.Allow));
        Assert.That(Process(policy, new SignRead { X = 100, Y = 100 }, PacketTypes.SignRead, Viewer),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(scenes.TryTakeVirtualSignRead(scene.Id, overlay, sign.Id, out var request), Is.True);
        Assert.That(request!.Value.Observer, Is.EqualTo(SceneSessionObserver.From(opened)));
        Assert.That(Process(policy, new SignNew { X = 100, Y = 100, Text = "Edited", PlayerId = (byte)Viewer.Slot }, PacketTypes.SignNew, Viewer),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(scenes.TryResolveVirtualSign(Arena, SceneSessionObserver.From(opened), 100, 100, out var current), Is.True);
        Assert.That(current!.Value.Definition.Text, Is.EqualTo("Welcome"));
    }

    [Test]
    public void OptedInVirtualSignEditRequiresPriorReadAndExplicitCurrentRevisionApproval()
    {
        var scenes = new SceneRuntime();
        var opened = new PlayerSessionOpened(PlayerSessionId.New(), Viewer, "Viewer", DateTimeOffset.UtcNow);
        scenes.OpenSession(Arena, opened);
        var player = new PlayerSnapshot(Viewer, "Viewer", false, 100, 100, 20, 20, new(1600, 1600), false, 0)
        { WorldId = Arena, SessionId = opened.SessionId };
        using var scene = scenes.CreateScene(Arena, "editable", "test");
        var sign = new SceneVirtualSignDefinition(VirtualSignId.New(), 0, 0, "Welcome", 1, true);
        var overlay = scene.AddOverlay(new SceneOverlayDefinition(default, "sign", new(100, 100, 2, 2), 1,
            SceneObserverScope.Global, SceneTileArea.Fill(2, 2, new SceneTile(55, Flags: SceneTileFlags.Active))) { VirtualSigns = [sign] });
        var policy = new SceneInteractionAuthorityPolicy(scenes, Arena, new Directory(player));
        void Edit(string text, byte playerId) => Assert.That(Process(policy, new SignNew { X = 100, Y = 100, Text = text, PlayerId = playerId },
            PacketTypes.SignNew, Viewer), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Edit("without read", (byte)Viewer.Slot);
        Assert.That(scenes.TryTakeVirtualSignEdit(scene.Id, overlay, sign.Id, out _), Is.False);
        Assert.That(Process(policy, new SignRead { X = 100, Y = 100 }, PacketTypes.SignRead, Viewer), Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Edit("wrong claimed player", (byte)(Viewer.Slot + 1));
        Assert.That(scenes.TryTakeVirtualSignEdit(scene.Id, overlay, sign.Id, out _), Is.False);
        Edit("new text", (byte)Viewer.Slot);
        Edit("second edit", (byte)Viewer.Slot);
        Assert.That(scenes.TryTakeVirtualSignEdit(scene.Id, overlay, sign.Id, out var request), Is.True);
        Assert.That(request!.Value.Text, Is.EqualTo("new text"));
        Assert.That(scenes.TryResolveVirtualSign(Arena, SceneSessionObserver.From(opened), 100, 100, out var current), Is.True);
        Assert.That(current!.Value.Definition.Text, Is.EqualTo("Welcome"));
        Assert.That(scenes.TryApplyVirtualSignEdit(player with { SessionId = PlayerSessionId.New() }, request.Value, out _), Is.False);
        Assert.That(scenes.TryApplyVirtualSignEdit(player with { Position = new(0, 0) }, request.Value, out _), Is.False);
        using (var covering = scenes.CreateScene(Arena, "cover", "test"))
        {
            covering.AddOverlay(new(default, "cover", new(101, 101, 1, 1), 5, SceneObserverScope.Global, SceneTileArea.Fill(1, 1, SceneTile.Air)));
            Assert.That(scenes.TryApplyVirtualSignEdit(player, request.Value, out _), Is.False);
        }
        Assert.That(scenes.TryApplyVirtualSignEdit(player, request.Value, out var updated), Is.True);
        Assert.That(updated!.Text, Is.EqualTo("new text"));
        Assert.That(updated.Revision, Is.EqualTo(2));
        Assert.That(scenes.TryApplyVirtualSignEdit(player, request.Value, out _), Is.False);
        Assert.That(scenes.TryTakeVirtualSignEdit(scene.Id, overlay, sign.Id, out _), Is.False);
        Assert.That(scenes.TryRequestVirtualSignRead(player, 100, 100), Is.True);
        Assert.That(scenes.TryRequestVirtualSignEdit(player, 100, 100, new string('X', 1025)), Is.False);
        Assert.That(scenes.TryRequestVirtualSignEdit(player, 100, 100, "pending"), Is.True);
        scenes.CloseSession(Arena, new(opened.SessionId, opened.PlayerId, opened.PlayerName, DateTimeOffset.UtcNow));
        scenes.OpenSession(Arena, opened with { SessionId = PlayerSessionId.New() });
        Assert.That(scenes.TryTakeVirtualSignEdit(scene.Id, overlay, sign.Id, out _), Is.False);
    }

    [Test]
    public void TileInteractionUsesTheSceneInTheOwnedWorld()
    {
        var scenes = new SceneRuntime();
        using var scene = scenes.CreateScene(Arena, "preview", "test");
        scene.AddOverlay(new SceneOverlayDefinition(default, "preview", new SceneBounds(100, 100, 1, 1), 1,
            SceneObserverScope.Personal([Viewer]), new SceneTileArea(1, 1, [new SceneTile(1, Flags: SceneTileFlags.Active)])));
        var tile = new Multiplicity.Packets.Tile { TileX = 100, TileY = 100, Action = TileAction.PlaceTile, Value = 1 };
        Assert.That(Process(new SceneInteractionAuthorityPolicy(scenes, Arena), tile, PacketTypes.Tile, Viewer),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.phantom_interaction")));
        Assert.That(Process(new SceneInteractionAuthorityPolicy(scenes, WorldId.Primary), tile, PacketTypes.Tile, Viewer),
            Is.EqualTo(PacketProcessingDecision.Allow));
        Assert.That(Process(new SceneInteractionAuthorityPolicy(scenes, Arena), tile, PacketTypes.Tile, new PlayerId(5)),
            Is.EqualTo(PacketProcessingDecision.Allow));
    }

    [Test]
    public void EntityVisibilityUsesTheSameWorldAsTheIncomingInteraction()
    {
        var visibility = new SceneVisibilityRuntime();
        using var layer = visibility.CreateLayer(Arena, "arena", "test", [Viewer]);
        layer.Assign(new SceneEntity(SceneEntityKind.Projectile, 7));
        var allowed = new ProjectileDestroy { Key = new ProjectileKey(4, 7, 1) };
        Assert.That(Process(new SceneEntityInteractionAuthorityPolicy(visibility, Arena), allowed, PacketTypes.ProjectileDestroy, Viewer),
            Is.EqualTo(PacketProcessingDecision.Allow));
        var other = new ProjectileDestroy { Key = new ProjectileKey(5, 7, 1) };
        Assert.That(Process(new SceneEntityInteractionAuthorityPolicy(visibility, Arena), other, PacketTypes.ProjectileDestroy, new PlayerId(5)),
            Is.EqualTo(PacketProcessingDecision.Reject("scene.hidden_entity_interaction")));
        Assert.That(Process(new SceneEntityInteractionAuthorityPolicy(visibility, WorldId.Primary), other, PacketTypes.ProjectileDestroy, new PlayerId(5)),
            Is.EqualTo(PacketProcessingDecision.Allow));
    }

    private static PacketProcessingDecision Process(IPacketProcessor<PacketView> policy, TerrariaPacket model, PacketTypes type, PlayerId player)
    {
        using var stream = new MemoryStream();
        model.ToStream(stream, includeHeader: false);
        var view = PacketView.FromPayload(type, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, PacketDirection.Inbound, new ConnectionId(player.Slot), player, DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class Directory(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
        public bool TryGet(PlayerId id, out PlayerSnapshot? result) { result = id == player.Id ? player : null; return result is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? result) { result = name == player.Name ? player : null; return result is not null; }
    }
}
