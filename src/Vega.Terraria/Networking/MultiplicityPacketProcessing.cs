using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Terraria.Networking;

/// <summary>
/// Adapts an exact Terraria packet payload into a Multiplicity view before entering Vega packet policy.
/// Raw buffer parsing remains inside the Terraria/protocol boundary.
/// </summary>
public static class MultiplicityPacketProcessing
{
    public static PacketProcessingDecision ProcessInboundPayload(
        byte rawPacketId,
        byte[] buffer,
        int offset,
        int payloadLength,
        ConnectionId connectionId,
        PlayerId? playerId,
        IPacketProcessor<PacketView> processor,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(processor);

        if (offset < 0 || payloadLength < 0 || offset > buffer.Length - payloadLength)
        {
            return PacketProcessingDecision.Reject("packet.invalid_payload");
        }

        ReadOnlySpan<byte> payload = new(buffer, offset, payloadLength);
        PacketView packet;
        if (TryGetProtocol325CompatibilityLength((PacketTypes)rawPacketId, out int expectedLength))
        {
            if (payload.Length != expectedLength)
            {
                return PacketProcessingDecision.Reject("packet.invalid_payload");
            }

            // Multiplicity 2.6.2 still carries the pre-1.4.5.7 layouts for these packets.
            // Keep the exception narrow and exact. Remove it after Vega consumes the audited package.
            packet = PacketView.FromPayload(rawPacketId, payload);
        }
        else if (!PacketViewParser.TryParsePayload(rawPacketId, payload, out packet))
        {
            return PacketProcessingDecision.Reject("packet.invalid_payload");
        }

        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            connectionId,
            playerId,
            timestamp,
            cancellationToken);

        return processor.Process(in context);
    }

    private static bool TryGetProtocol325CompatibilityLength(PacketTypes packetType, out int payloadLength)
    {
        payloadLength = packetType switch
        {
            PacketTypes.PlayerSpawn => 15,
            PacketTypes.TileGetSection => 9,
            PacketTypes.NpcAddBuff => 6,
            PacketTypes.PlayerAddBuff => 7,
            _ => 0
        };
        return payloadLength != 0;
    }
}
