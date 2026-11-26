using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Terraria.Networking;

/// <summary>
/// Adapts Terraria payload windows into Multiplicity packet views before entering Vega packet policy.
/// Raw buffer parsing remains inside the Terraria/protocol boundary.
/// </summary>
public static class MultiplicityPacketProcessing
{
    public static PacketProcessingDecision ProcessInboundPayload(
        byte rawPacketId,
        byte[] buffer,
        int offset,
        int availableLength,
        ConnectionId connectionId,
        PlayerId? playerId,
        IPacketProcessor<PacketView> processor,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(processor);

        // The memory overload keeps variable-length parsing on the existing buffer
        // instead of materializing a copy of the entire remaining receive buffer.
        // Give it only the packet window while preserving the bounded parser's
        // contract of accepting a valid payload prefix.
        if (!TryParseInboundPayload(rawPacketId, buffer, offset, availableLength, out var packet))
        {
            return new PacketProcessingDecision(PacketDecisionKind.Reject, "packet.invalid_payload");
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

    internal static bool TryParseInboundPayload(byte rawPacketId, byte[] buffer, int offset, int availableLength, out PacketView packet)
    {
        packet = default;
        return offset >= 0 && availableLength >= 0 && offset <= buffer.Length && availableLength <= buffer.Length - offset &&
            PacketViewParser.TryParsePayload(rawPacketId, buffer.AsMemory(offset, availableLength), out packet, out _);
    }
}
