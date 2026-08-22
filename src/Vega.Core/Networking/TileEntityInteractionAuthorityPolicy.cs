using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class TileEntityInteractionAuthorityPolicy(IWorldInteractionState world) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        int? tileEntityId = context.Packet.PacketType switch
        {
            PacketTypes.RequestTileEntityInteraction =>
                context.Packet.AsRequestTileEntityInteractionView().TileEntityId,
            PacketTypes.TileEntityDisplayDollItemSync =>
                context.Packet.AsTileEntityDisplayDollItemSyncView().TileEntityId,
            PacketTypes.TileEntityHatRackItemSync =>
                context.Packet.AsTileEntityHatRackItemSyncView().TileEntityId,
            _ => null
        };
        return tileEntityId is null || world.TryGetTileEntityPosition(tileEntityId.Value, out _)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.tile_entity.not_found");
    }
}
