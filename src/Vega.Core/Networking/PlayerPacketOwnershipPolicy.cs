using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects inbound player-state packets that try to mutate another connection's player slot.
/// </summary>
public sealed class PlayerPacketOwnershipPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return new PacketProcessingDecision(PacketDecisionKind.Continue);
        }

        int? claimedSlot = context.Packet.PacketType switch
        {
            PacketTypes.PlayerHp => context.Packet.AsPlayerHpView().PlayerId,
            PacketTypes.PlayerMana => context.Packet.AsPlayerManaView().PlayerId,
            PacketTypes.PlayerTeam => context.Packet.AsPlayerTeamView().PlayerId,
            PacketTypes.TeamChangeFromUI => context.Packet.AsTeamChangeFromUIView().PlayerId,
            PacketTypes.TogglePvp => context.Packet.AsTogglePvpView().PlayerId,
            PacketTypes.SpectatePlayer => context.Packet.AsSpectatePlayerView().PlayerId,
            PacketTypes.PlayerDeathV2 => context.Packet.AsPlayerDeathV2View().PlayerId,
            _ => null
        };

        return claimedSlot is not null && claimedSlot != context.ConnectionId.Slot
            ? new PacketProcessingDecision(
                PacketDecisionKind.Violation,
                "packet.player_identity_mismatch")
            : new PacketProcessingDecision(PacketDecisionKind.Continue);
    }
}
