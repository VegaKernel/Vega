using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Terraria.Networking;

/// <summary>
/// Adapts an exact Terraria packet payload into a Multiplicity view before entering Vega packet policy.
/// Raw buffer parsing remains inside the Terraria/protocol boundary. Security ingress intentionally uses
/// exact <see cref="PacketViewParser.TryParsePayload(byte, ReadOnlySpan{byte}, out PacketView)"/> semantics;
/// <c>PacketViewParser.TryParsePayloadBounded</c> is reserved for APIs that expose an upper-bound window.
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
        if (!PacketViewParser.TryParsePayload(rawPacketId, payload, out PacketView packet))
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
}
