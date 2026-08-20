using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using TShockNG.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects inbound player-health packets that try to mutate another connection's player slot.
/// </summary>
public sealed class PlayerHpOwnershipPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Packet.PacketType != PacketTypes.PlayerHp)
        {
            return new PacketProcessingDecision(PacketDecisionKind.Continue);
        }

        var packet = context.Packet.AsPlayerHpView();
        if (packet.PlayerId != context.ConnectionId.Slot)
        {
            return new PacketProcessingDecision(
                PacketDecisionKind.Reject,
                "packet.player_identity_mismatch");
        }

        return new PacketProcessingDecision(PacketDecisionKind.Continue);
    }
}
