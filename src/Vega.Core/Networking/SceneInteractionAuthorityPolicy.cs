using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class SceneInteractionAuthorityPolicy(ISceneRuntime scenes, WorldId? ownedWorld = null) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || !TryGetPosition(context.Packet, out int x, out int y))
        {
            return PacketProcessingDecision.Allow;
        }

        return context.PlayerId is PlayerId playerId && scenes.TryResolveTile(
            ownedWorld ?? WorldId.Primary,
            playerId,
            x,
            y,
            out _,
            out _)
            ? PacketProcessingDecision.Reject("scene.phantom_interaction")
            : PacketProcessingDecision.Allow;
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
            PacketTypes.LiquidSet => (packet.AsLiquidSetView().X, packet.AsLiquidSetView().Y),
            PacketTypes.PlaceTileEntity => (packet.AsPlaceTileEntityView().X, packet.AsPlaceTileEntityView().Y),
            _ => (int.MinValue, int.MinValue)
        };
        return x != int.MinValue;
    }
}
