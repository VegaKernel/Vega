using Vega.Contracts.Players;

namespace Vega.Contracts.Networking;

/// <summary>
/// Stack-only processing context for a typed packet and the runtime metadata that accompanies it.
/// </summary>
/// <typeparam name="TPacket">A typed packet value, including stack-only packet views.</typeparam>
public readonly ref struct PacketProcessingContext<TPacket>
    where TPacket : allows ref struct
{
    public PacketProcessingContext(
        TPacket packet,
        PacketDirection direction,
        ConnectionId connectionId,
        PlayerId? playerId,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        Packet = packet;
        Direction = direction;
        ConnectionId = connectionId;
        PlayerId = playerId;
        Timestamp = timestamp;
        CancellationToken = cancellationToken;
    }

    public TPacket Packet { get; }

    public PacketDirection Direction { get; }

    public ConnectionId ConnectionId { get; }

    public PlayerId? PlayerId { get; }

    public DateTimeOffset Timestamp { get; }

    public CancellationToken CancellationToken { get; }
}
