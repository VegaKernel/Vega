using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

public sealed class TerrariaConnectionMetadataRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<int, MutableMetadata> connections = [];

    public PacketProcessingDecision ObserveInbound(
        byte rawPacketId,
        ReadOnlySpan<byte> payload,
        int senderSlot,
        TerrariaConnectionState connectionState)
    {
        if (rawPacketId == (byte)PacketTypes.ConnectRequest)
        {
            lock (sync)
            {
                connections.Remove(senderSlot);
            }

            return Allow();
        }

        if (rawPacketId is not ((byte)PacketTypes.ClientUUID) and
            not ((byte)PacketTypes.PlayerPlatformInfo))
        {
            return Allow();
        }

        PacketView packet = PacketView.FromPayload(rawPacketId, payload);

        lock (sync)
        {
            if (!connections.TryGetValue(senderSlot, out MutableMetadata? metadata))
            {
                metadata = new MutableMetadata();
                connections.Add(senderSlot, metadata);
            }

            return packet.PacketType == PacketTypes.ClientUUID
                ? ObserveUuid(packet, connectionState, metadata)
                : ObservePlatform(packet, senderSlot, connectionState, metadata);
        }
    }

    public ConnectionMetadataSnapshot Snapshot(
        int slot,
        string? transportEndpoint,
        TerrariaConnectionState state)
    {
        lock (sync)
        {
            connections.TryGetValue(slot, out MutableMetadata? metadata);
            return new ConnectionMetadataSnapshot(
                new ConnectionId(slot),
                transportEndpoint,
                transportEndpoint,
                ConnectionForwardingProvenance.Direct,
                metadata?.Platform ?? ClientPlatformInfo.Unknown,
                metadata?.ClientUuid,
                ToLoginPhase(state));
        }
    }

    public void Remove(int slot)
    {
        lock (sync)
        {
            connections.Remove(slot);
        }
    }

    private static PacketProcessingDecision ObserveUuid(
        PacketView packet,
        TerrariaConnectionState state,
        MutableMetadata metadata)
    {
        if (state != TerrariaConnectionState.ProtocolAccepted || metadata.ClientUuid is not null)
        {
            return Reject("connection_metadata.uuid_wrong_phase");
        }

        try
        {
            string value = packet.AsClientUUIDView().Uuid;
            if (packet.PayloadSpan.Length != 37 || !Guid.TryParse(value, out Guid uuid))
            {
                return Reject("connection_metadata.uuid_invalid");
            }

            metadata.ClientUuid = uuid.ToString("D");
            return Allow();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            return Reject("connection_metadata.uuid_invalid");
        }
    }

    private static PacketProcessingDecision ObservePlatform(
        PacketView packet,
        int senderSlot,
        TerrariaConnectionState state,
        MutableMetadata metadata)
    {
        if (state < TerrariaConnectionState.ProtocolAccepted || packet.PayloadSpan.Length != 2)
        {
            return Reject("connection_metadata.platform_invalid");
        }

        byte claimedSlot = packet.PayloadSpan[0];
        if (claimedSlot != senderSlot)
        {
            return new PacketProcessingDecision(
                PacketDecisionKind.Violation,
                "connection_metadata.platform_sender_mismatch");
        }

        byte reportedCode = packet.PayloadSpan[1];
        var platform = new ClientPlatformInfo(ToPlatform(reportedCode), reportedCode);
        if (metadata.Platform.Kind != ClientPlatformKind.Unknown && metadata.Platform != platform)
        {
            return Reject("connection_metadata.platform_changed");
        }

        metadata.Platform = platform;
        return Allow();
    }

    private static ClientPlatformKind ToPlatform(byte value) => value switch
    {
        0 => ClientPlatformKind.Mobile,
        1 => ClientPlatformKind.Stadia,
        2 => ClientPlatformKind.Xbox,
        3 => ClientPlatformKind.PlayStation,
        4 => ClientPlatformKind.Editor,
        5 => ClientPlatformKind.Switch,
        233 => ClientPlatformKind.Pc,
        _ => ClientPlatformKind.Unrecognized
    };

    private static ConnectionLoginPhase ToLoginPhase(TerrariaConnectionState state) => state switch
    {
        >= TerrariaConnectionState.FullyConnected => ConnectionLoginPhase.FullyConnected,
        >= TerrariaConnectionState.SendingWorld => ConnectionLoginPhase.SendingWorld,
        >= TerrariaConnectionState.WorldRequested => ConnectionLoginPhase.WorldRequested,
        >= TerrariaConnectionState.ProtocolAccepted => ConnectionLoginPhase.ProtocolAccepted,
        _ => ConnectionLoginPhase.Connected
    };

    private static PacketProcessingDecision Allow() => new(PacketDecisionKind.Allow);

    private static PacketProcessingDecision Reject(string reason) =>
        new(PacketDecisionKind.Reject, reason);

    private sealed class MutableMetadata
    {
        public ClientPlatformInfo Platform { get; set; } = ClientPlatformInfo.Unknown;

        public string? ClientUuid { get; set; }
    }
}
