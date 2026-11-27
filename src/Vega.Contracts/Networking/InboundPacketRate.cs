namespace Vega.Contracts.Networking;

/// <summary>Attempts in this connection's current fixed one-second window, including the current frame when delivered to a policy.</summary>
public readonly record struct InboundPacketRate(ConnectionId Connection, byte PacketId, int TypeCount, int TotalCount);

public interface IInboundPacketRateReader
{
    /// <summary>Returns null for an unoccupied slot. Counts are zero after an idle second.</summary>
    InboundPacketRate? Read(ConnectionId connection, byte packetId);
}
