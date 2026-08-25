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

        if (!PacketViewParser.TryParsePayload(
                rawPacketId,
                buffer,
                offset,
                payloadLength,
                out PacketView packet))
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
