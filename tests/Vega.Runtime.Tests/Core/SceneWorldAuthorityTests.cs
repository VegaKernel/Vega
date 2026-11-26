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
}
