using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Retains Terraria-version-specific validation for NPC coin extra-value requests.
/// The packet is legitimate client gameplay in expert/master worlds, but its NPC proximity
/// must be checked against live native state before Terraria mutates <c>NPC.extraValue</c>.
/// </summary>
public sealed class TerrariaNpcCoinPickupHardeningPolicy : IPacketProcessor<PacketView>
{
    private const float MaximumDistancePixels = 5 * 16f;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.SyncExtraValue)
        {
            return PacketProcessingDecision.Allow;
        }

        if (!global::Terraria.Main.expertMode && !global::Terraria.Main.masterMode)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.world_mode_not_supported");
        }

        ReadOnlySpan<byte> payload = context.Packet.PayloadSpan;
        if (payload.Length != 14)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.malformed");
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

        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[6..]));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
        float deltaX = x - npc.position.X;
        float deltaY = y - npc.position.Y;
        float distanceSquared = deltaX * deltaX + deltaY * deltaY;
        return distanceSquared >= MaximumDistancePixels * MaximumDistancePixels
            ? PacketProcessingDecision.Reject("packet.npc_coin_pickup.too_far_from_npc")
            : PacketProcessingDecision.Allow;
    }
}
