using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Keeps NPC coin-pickup state server-authoritative by rejecting client-originated updates.
/// </summary>
public sealed class NpcCoinPickupAuthorityPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context) =>
        context.Direction == PacketDirection.Inbound &&
        context.Packet.PacketType == PacketTypes.SyncExtraValue
            ? new PacketProcessingDecision(
                PacketDecisionKind.Reject,
                "packet.npc_coin_pickup_server_authoritative")
            : new PacketProcessingDecision(PacketDecisionKind.Continue);
}
