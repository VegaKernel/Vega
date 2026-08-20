namespace Vega.Contracts.Networking;

/// <summary>
/// Processes a typed packet inside the Vega packet-processing boundary.
/// </summary>
/// <typeparam name="TPacket">A typed packet value. Stack-only packet views are supported.</typeparam>
public interface IPacketProcessor<TPacket>
    where TPacket : allows ref struct
{
    PacketProcessingDecision Process(scoped in PacketProcessingContext<TPacket> context);
}
