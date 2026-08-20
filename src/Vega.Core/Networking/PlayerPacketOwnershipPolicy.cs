using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using TShockNG.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects inbound player-state packets that try to mutate another connection's player slot.
/// </summary>
public sealed class PlayerPacketOwnershipPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        int? claimedSlot = context.Packet.PacketType switch
        {
            PacketTypes.PlayerHp => context.Packet.AsPlayerHpView().PlayerId,
            PacketTypes.PlayerMana => context.Packet.AsPlayerManaView().PlayerId,
            PacketTypes.PlayerTeam => context.Packet.AsPlayerTeamView().PlayerId,
            PacketTypes.TogglePvp => context.Packet.AsTogglePvpView().PlayerId,
            _ => null
        };

        return claimedSlot is not null && claimedSlot != context.ConnectionId.Slot
            ? new PacketProcessingDecision(
                PacketDecisionKind.Reject,
                "packet.player_identity_mismatch")
            : new PacketProcessingDecision(PacketDecisionKind.Continue);
    }
}
