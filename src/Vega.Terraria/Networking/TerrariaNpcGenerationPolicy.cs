using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Rejects stale NPC strike generations using Terraria's live NPC slot state.
/// A generation mismatch is expected network staleness, not by itself a malicious violation.
/// </summary>
public sealed class TerrariaNpcGenerationPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || context.Packet.PacketType != PacketTypes.NpcStrike)
        {
            return PacketProcessingDecision.Allow;
        }

        var strike = new NpcStrikeView(context.Packet);
        if (strike.NpcSlot >= global::Terraria.Main.npc.Length)
        {
            return PacketProcessingDecision.Violation("packet.npc.strike_id_out_of_range");
        }

        global::Terraria.NPC npc = global::Terraria.Main.npc[strike.NpcSlot];
        return npc.generation != strike.Generation
            ? PacketProcessingDecision.Reject("packet.npc.stale_generation")
            : PacketProcessingDecision.Allow;
    }
}
