using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Applies NPC checks that require Terraria's live NPC array rather than protocol-only ranges.
/// </summary>
public sealed class TerrariaNpcGenerationPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.NpcStrike => ValidateStrike(new NpcStrikeView(context.Packet)),
            PacketTypes.CatchNPC => ValidateCatch(new CatchNPCView(context.Packet)),
            _ => PacketProcessingDecision.Allow
        };
    }

    private static PacketProcessingDecision ValidateStrike(NpcStrikeView strike)
    {
        if (strike.NpcSlot >= global::Terraria.Main.npc.Length)
        {
            return PacketProcessingDecision.Violation("packet.npc.strike_id_out_of_range");
        }

        global::Terraria.NPC npc = global::Terraria.Main.npc[strike.NpcSlot];
        return npc.generation != strike.Generation
            ? PacketProcessingDecision.Reject("packet.npc.stale_generation")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateCatch(CatchNPCView value)
    {
        if (value.NpcId < 0 || value.NpcId >= global::Terraria.Main.npc.Length)
        {
            return PacketProcessingDecision.Violation("packet.npc.catch_id_out_of_range");
        }

        global::Terraria.NPC npc = global::Terraria.Main.npc[value.NpcId];
        return npc.catchItem == 0
            ? PacketProcessingDecision.Reject("packet.npc.catch_not_catchable")
            : PacketProcessingDecision.Allow;
    }
}
