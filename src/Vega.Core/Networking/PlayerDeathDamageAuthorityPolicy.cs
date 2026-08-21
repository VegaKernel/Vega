using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects abnormal client-authored death damage before Terraria processes it.
/// </summary>
public sealed class PlayerDeathDamageAuthorityPolicy : IPacketProcessor<PacketView>
{
    public const ushort MaximumAcceptedDamage = 42_000;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.PlayerDeathV2)
        {
            return PacketProcessingDecision.Allow;
        }

        ushort damage = unchecked((ushort)context.Packet.AsPlayerDeathV2View().Damage);
        return damage > MaximumAcceptedDamage
            ? PacketProcessingDecision.Violation("packet.player_death.damage_out_of_range")
            : PacketProcessingDecision.Allow;
    }
}
