using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Validates protocol-level NPC coin extra-value fields before Terraria-specific live-state checks run.
/// Protocol 326 encodes the accumulated delta as Int32 even though Multiplicity 2.6.3 exposes that field as Single.
/// </summary>
public sealed class NpcCoinPickupAuthorityPolicy : IPacketProcessor<PacketView>
{
    private const int PayloadLength = 14;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.SyncExtraValue)
        {
            return PacketProcessingDecision.Allow;
        }

        ReadOnlySpan<byte> payload = context.Packet.PayloadSpan;
        if (payload.Length != PayloadLength)
        {
            return PacketProcessingDecision.Reject("packet.npc_coin_pickup.malformed");
        }

        short npcIndex = BinaryPrimitives.ReadInt16LittleEndian(payload);
        if (npcIndex < 0)
        {
            return PacketProcessingDecision.Violation("packet.npc_coin_pickup.npc_out_of_range");
        }

        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[6..]));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
        return !float.IsFinite(x) || !float.IsFinite(y)
            ? PacketProcessingDecision.Violation("packet.npc_coin_pickup.non_finite_position")
            : PacketProcessingDecision.Allow;
    }
}
