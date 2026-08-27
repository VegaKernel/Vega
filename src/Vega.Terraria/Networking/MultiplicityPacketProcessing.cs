using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Terraria.Networking;

/// <summary>
/// Adapts an exact Terraria packet payload into a Multiplicity view before entering Vega packet policy.
/// Raw buffer parsing remains inside the Terraria/protocol boundary. Security ingress intentionally uses
/// exact <see cref="PacketViewParser.TryParsePayload(byte, ReadOnlySpan{byte}, out PacketView)"/> semantics,
/// except for protocol-326 layouts that are newer than the currently published Multiplicity schema.
/// <c>PacketViewParser.TryParsePayloadBounded</c> is reserved for APIs that expose an upper-bound window.
/// </summary>
public static class MultiplicityPacketProcessing
{
    private const int Protocol326ZonesPayloadLength = 7;
    private const int Protocol326TeleportItemPayloadLength = 1;

    public static PacketProcessingDecision ProcessInboundPayload(
        byte rawPacketId,
        ReadOnlySpan<byte> payload,
        ConnectionId connectionId,
        PlayerId? playerId,
        IPacketProcessor<PacketView> processor,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processor);

        if (!TryParseInboundPayload(rawPacketId, payload, out PacketView packet))
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

        return ProcessInboundPayload(
            rawPacketId,
            new ReadOnlySpan<byte>(buffer, offset, payloadLength),
            connectionId,
            playerId,
            processor,
            timestamp,
            cancellationToken);
    }

    private static bool TryParseInboundPayload(
        byte rawPacketId,
        ReadOnlySpan<byte> payload,
        out PacketView packet)
    {
        PacketTypes packetType = (PacketTypes)rawPacketId;
        int? protocol326Length = packetType switch
        {
            // Protocol 326 adds zone5 and townNPCs after the four legacy zone bytes.
            PacketTypes.Zones => Protocol326ZonesPayloadLength,
            // Protocol 326 carries the teleport-item subtype (potion/conch/shellphone/recovery).
            PacketTypes.TeleportationPotion => Protocol326TeleportItemPayloadLength,
            _ => null
        };

        if (protocol326Length is int expectedLength)
        {
            if (payload.Length != expectedLength)
            {
                packet = default;
                return false;
            }

            packet = PacketView.FromPayload(packetType, payload);
            return true;
        }

        return PacketViewParser.TryParsePayload(rawPacketId, payload, out packet);
    }
}
