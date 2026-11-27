using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class SceneInteractionAuthorityPolicy(ISceneRuntime scenes, WorldId? ownedWorld,
    IPlayerDirectory? players) : IPacketProcessor<PacketView>
{
    public SceneInteractionAuthorityPolicy(ISceneRuntime scenes, WorldId? ownedWorld = null) : this(scenes, ownedWorld, null) { }

    public PacketProcessingDecision Process(in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || !TryGetPosition(context.Packet, out int x, out int y))
        {
            return PacketProcessingDecision.Allow;
        }

        WorldId world = ownedWorld ?? WorldId.Primary;
        if (context.PlayerId is not PlayerId playerId || !scenes.TryResolveTile(
            world,
            playerId,
            x,
            y,
            out _,
            out _)) return PacketProcessingDecision.Allow;
        // A visible virtual object may produce a plugin-owned intent, but
        // the canonical interaction is still rejected. No native chest, sign
        // or item mutation is performed by this observation path.
        if (context.Packet.PacketType is PacketTypes.ChestGetContents or PacketTypes.SignRead or PacketTypes.SignNew && players is not null &&
            players.TryGet(playerId, out var player) && player is not null && player.WorldId == world)
        {
            if (context.Packet.PacketType == PacketTypes.ChestGetContents) scenes.TryRequestVirtualChestOpen(player, x, y);
            else if (context.Packet.PacketType == PacketTypes.SignRead) scenes.TryRequestVirtualSignRead(player, x, y);
            else
            {
                var edit = context.Packet.AsSignNewView();
                if (edit.PlayerId == playerId.Slot) scenes.TryRequestVirtualSignEdit(player, x, y, edit.Text);
            }
        }
        return PacketProcessingDecision.Reject("scene.phantom_interaction");
    }

    private static bool TryGetPosition(PacketView packet, out int x, out int y)
    {
        (x, y) = packet.PacketType switch
        {
            PacketTypes.Tile => (packet.AsTileView().TileX, packet.AsTileView().TileY),
            PacketTypes.PlaceObject => (packet.AsPlaceObjectView().X, packet.AsPlaceObjectView().Y),
            PacketTypes.PaintTile => (packet.AsPaintTileView().X, packet.AsPaintTileView().Y),
            PacketTypes.PaintWall => (packet.AsPaintWallView().X, packet.AsPaintWallView().Y),
            PacketTypes.PlaceChest => (packet.AsPlaceChestView().TileX, packet.AsPlaceChestView().TileY),
            PacketTypes.ChestGetContents => (packet.AsChestGetContentsView().TileX, packet.AsChestGetContentsView().TileY),
            PacketTypes.ChestUnlock => (packet.AsChestUnlockView().X, packet.AsChestUnlockView().Y),
            PacketTypes.SignRead => (packet.AsSignReadView().X, packet.AsSignReadView().Y),
            PacketTypes.SignNew => (packet.AsSignNewView().X, packet.AsSignNewView().Y),
            PacketTypes.LiquidSet => (packet.AsLiquidSetView().X, packet.AsLiquidSetView().Y),
            PacketTypes.PlaceTileEntity => (packet.AsPlaceTileEntityView().X, packet.AsPlaceTileEntityView().Y),
            PacketTypes.PlaceItemFrame => (packet.AsPlaceItemFrameView().X, packet.AsPlaceItemFrameView().Y),
            _ => (int.MinValue, int.MinValue)
        };
        return x != int.MinValue;
    }
}
