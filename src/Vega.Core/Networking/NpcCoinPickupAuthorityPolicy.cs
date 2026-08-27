using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Validates client-authored NPC coin extra-value requests before Terraria applies and rebroadcasts
/// the authoritative accumulated value.
/// </summary>
public sealed class NpcCoinPickupAuthorityPolicy(
    int npcCount,
    Func<int> worldWidth,
    Func<int> worldHeight) : IPacketProcessor<PacketView>
{
    private const int PayloadLength = 14;
    private const int TileSize = 16;

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
        if (npcIndex < 0 || npcIndex >= npcCount)
        {
            return PacketProcessingDecision.Violation("packet.npc_coin_pickup.npc_out_of_range");
        }

        float x = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[6..]));
        float y = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload[10..]));
        if (!float.IsFinite(x) || !float.IsFinite(y))
        {
            return PacketProcessingDecision.Violation("packet.npc_coin_pickup.non_finite_position");
        }

        int width = worldWidth();
        int height = worldHeight();
        return width <= 0 || height <= 0 ||
            x < 0 || y < 0 || x >= width * TileSize || y >= height * TileSize
            ? PacketProcessingDecision.Violation("packet.npc_coin_pickup.position_out_of_world")
            : PacketProcessingDecision.Allow;
    }
}
