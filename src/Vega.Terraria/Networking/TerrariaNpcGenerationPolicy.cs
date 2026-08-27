using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Applies NPC checks that require Terraria's live NPC array rather than protocol-only ranges.
/// </summary>
public sealed class TerrariaNpcGenerationPolicy : IPacketProcessor<PacketView>
{
    private const float MaximumCoinPickupDistancePixels = 5 * 16f;

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
            PacketTypes.SyncExtraValue => ValidateCoinPickup(context.Packet),
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

    private static PacketProcessingDecision ValidateCoinPickup(PacketView packet)
    {
        ReadOnlySpan<byte> payload = packet.PayloadSpan;
        if (payload.Length != 14)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.malformed");
        }

        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[6..]));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            x < 0 || y < 0 ||
            x >= global::Terraria.Main.maxTilesX * 16f ||
            y >= global::Terraria.Main.maxTilesY * 16f)
        {
            return PacketProcessingDecision.Violation("packet.npc_coin_pickup.position_out_of_world");
        }

        if (!global::Terraria.Main.expertMode && !global::Terraria.Main.masterMode)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.world_mode_not_supported");
        }

        short npcIndex = BinaryPrimitives.ReadInt16LittleEndian(payload);
        if (npcIndex < 0 || npcIndex >= global::Terraria.Main.npc.Length)
        {
            return PacketProcessingDecision.Violation("packet.npc_coin_pickup.npc_out_of_range");
        }

        global::Terraria.NPC? npc = global::Terraria.Main.npc[npcIndex];
        if (npc is null)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.npc_not_found");
        }

        float deltaX = x - npc.position.X;
        float deltaY = y - npc.position.Y;
        float distanceSquared = deltaX * deltaX + deltaY * deltaY;
        return distanceSquared >= MaximumCoinPickupDistancePixels * MaximumCoinPickupDistancePixels
            ? PacketProcessingDecision.Reject("packet.npc_coin_pickup.too_far_from_npc")
            : PacketProcessingDecision.Allow;
    }
}
