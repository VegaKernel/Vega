namespace TShockNG.Contracts.Networking;

public interface IPacketProcessor
{
    PacketProcessingDecision Process(int playerId, int packetType, ReadOnlyMemory<byte> payload);
}
