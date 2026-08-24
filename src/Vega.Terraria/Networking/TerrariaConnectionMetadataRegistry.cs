using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using System.Net;

namespace Vega.Terraria.Networking;

public sealed class TerrariaConnectionMetadataRegistry
{
    private const int MaxClientUuidLength = 128;

    private readonly object sync = new();
    private readonly Dictionary<int, MutableMetadata> connections = [];
    private bool dimensionsEnabled;
    private HashSet<IPAddress> trustedDimensionsPeers = [];
    private ISandboxTransferAdmissionService? sandboxAdmission;
    private WorldId sandboxAdmissionWorldId = WorldId.Primary;
    private bool sandboxAdmissionRequired;

    public void ConfigureDimensions(bool enabled, IEnumerable<string> trustedPeers)
    {
        ArgumentNullException.ThrowIfNull(trustedPeers);
        var parsed = new HashSet<IPAddress>();
        foreach (string value in trustedPeers)
        {
            if (!IPAddress.TryParse(value, out IPAddress? address) || address.IsIPv6Multicast)
            {
                throw new InvalidDataException($"Dimensions trusted proxy '{value}' is not a valid IP address.");
            }

            parsed.Add(address.MapToIPv6());
        }

        lock (sync)
        {
            dimensionsEnabled = enabled;
            trustedDimensionsPeers = parsed;
        }
    }

    public void ConfigureSandboxAdmission(
        ISandboxTransferAdmissionService? admission,
        WorldId receivingWorldId,
        bool required)
    {
        lock (sync)
        {
            sandboxAdmission = admission;
            sandboxAdmissionWorldId = receivingWorldId;
            sandboxAdmissionRequired = required;
        }
    }

    public PacketProcessingDecision ObserveInbound(
        byte rawPacketId,
        ReadOnlySpan<byte> payload,
        int senderSlot,
        TerrariaConnectionState connectionState) =>
        ObserveInboundDetailed(rawPacketId, payload, senderSlot, connectionState, null).Decision;

    public ConnectionMetadataObservation ObserveInboundDetailed(
        byte rawPacketId,
        ReadOnlySpan<byte> payload,
        int senderSlot,
        TerrariaConnectionState connectionState,
        string? transportEndpoint)
    {
        if (rawPacketId == (byte)PacketTypes.ConnectRequest)
        {
            lock (sync)
            {
                connections.Remove(senderSlot);
            }

            return new ConnectionMetadataObservation(Allow(), false);
        }

        if (rawPacketId == (byte)PacketTypes.Placeholder)
        {
            return ObserveDimensions(payload, senderSlot, transportEndpoint);
        }

        if (rawPacketId is not ((byte)PacketTypes.ClientUUID) and not ((byte)PacketTypes.PlayerPlatformInfo))
        {
            return new ConnectionMetadataObservation(Allow(), false);
        }

        PacketView packet = PacketView.FromPayload(rawPacketId, payload);

        lock (sync)
        {
            if (!connections.TryGetValue(senderSlot, out MutableMetadata? metadata))
            {
                metadata = new MutableMetadata();
                connections.Add(senderSlot, metadata);
            }

            PacketProcessingDecision decision = packet.PacketType == PacketTypes.ClientUUID
                ? ObserveUuid(packet, connectionState, metadata)
                : ObservePlatform(packet, senderSlot, connectionState, metadata);
            if (decision.Kind == PacketDecisionKind.Allow &&
                packet.PacketType == PacketTypes.ClientUUID &&
                sandboxAdmission is not null &&
                (sandboxAdmissionRequired ||
                 metadata.ForwardingProvenance == ConnectionForwardingProvenance.TrustedForwarder))
            {
                decision = ConsumeSandboxAdmission(metadata, sandboxAdmissionRequired);
            }

            return new ConnectionMetadataObservation(decision, false);
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
                metadata?.EffectiveEndpoint ?? transportEndpoint,
                metadata?.ForwardingProvenance ?? ConnectionForwardingProvenance.Direct,
                metadata?.Platform ?? ClientPlatformInfo.Unknown,
                metadata?.ClientUuid,
                ToLoginPhase(state))
            {
                TrustedTransferAccountId = metadata?.TrustedTransferAccountId
            };
        }
    }

    private ConnectionMetadataObservation ObserveDimensions(
        ReadOnlySpan<byte> payload,
        int senderSlot,
        string? transportEndpoint)
    {
        lock (sync)
        {
            if (!dimensionsEnabled)
            {
                return new ConnectionMetadataObservation(Allow(), false);
            }

            if (!TryGetAddress(transportEndpoint, out IPAddress? peer) || peer is null ||
                !trustedDimensionsPeers.Contains(peer.MapToIPv6()))
            {
                return new ConnectionMetadataObservation(
                    Reject("connection_metadata.dimensions_untrusted_peer"),
                    true);
            }

            try
            {
                PacketView packet = PacketView.FromPayload(PacketTypes.Placeholder, payload);
                PacketReader reader = packet.CreatePayloadReader();
                short joinType = reader.ReadInt16();
                if (joinType != 0)
                {
                    return new ConnectionMetadataObservation(Allow(), true);
                }

                string forwarded = reader.ReadStringUtf8();
                reader.EnsureEnd();
                if (!IPAddress.TryParse(forwarded, out IPAddress? effective) ||
                    effective.Equals(IPAddress.Any) || effective.Equals(IPAddress.IPv6Any) ||
                    effective.IsIPv6Multicast)
                {
                    return new ConnectionMetadataObservation(
                        Reject("connection_metadata.dimensions_invalid_address"),
                        true);
                }

                if (!connections.TryGetValue(senderSlot, out MutableMetadata? metadata))
                {
                    metadata = new MutableMetadata();
                    connections.Add(senderSlot, metadata);
                }

                metadata.EffectiveEndpoint = effective.ToString();
                metadata.ForwardingProvenance = ConnectionForwardingProvenance.TrustedForwarder;
                return new ConnectionMetadataObservation(Allow(), true);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
            {
                return new ConnectionMetadataObservation(
                    Reject("connection_metadata.dimensions_malformed"),
                    true);
            }
        }
    }

    private static bool TryGetAddress(string? endpoint, out IPAddress? address)
    {
        if (IPAddress.TryParse(endpoint, out address))
        {
            return true;
        }

        if (endpoint is not null && IPEndPoint.TryParse(endpoint, out IPEndPoint? parsed))
        {
            address = parsed.Address;
            return true;
        }

        address = null;
        return false;
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
            PacketReader reader = packet.CreatePayloadReader();
            string value = reader.ReadStringUtf8();
            reader.EnsureEnd();
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxClientUuidLength)
            {
                return Reject("connection_metadata.uuid_invalid");
            }

            // Terraria persists ClientUUID as an opaque string. Preserve the exact token so
            // reconnect, transfer-ticket and moderation comparisons use vanilla semantics.
            metadata.ClientUuid = value;
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

    private PacketProcessingDecision ConsumeSandboxAdmission(MutableMetadata metadata, bool required)
    {
        if (sandboxAdmission is null ||
            string.IsNullOrWhiteSpace(metadata.EffectiveEndpoint) ||
            string.IsNullOrWhiteSpace(metadata.ClientUuid))
        {
            return required
                ? Reject("sandbox.transfer.admission_required")
                : Allow();
        }

        SandboxTransferAdmissionResult result = sandboxAdmission.Consume(
            sandboxAdmissionWorldId,
            metadata.EffectiveEndpoint,
            metadata.ClientUuid,
            DateTimeOffset.UtcNow);
        if (!result.Accepted)
        {
            if (!required && result.ErrorCode == "sandbox.transfer.admission_required")
            {
                return Allow();
            }

            return Reject(result.ErrorCode ?? "sandbox.transfer.admission_rejected");
        }

        metadata.TrustedTransferAccountId = result.Ticket?.AccountId;
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
        public string? EffectiveEndpoint { get; set; }
        public ConnectionForwardingProvenance ForwardingProvenance { get; set; } = ConnectionForwardingProvenance.Direct;
        public string? TrustedTransferAccountId { get; set; }
    }
}

public readonly record struct ConnectionMetadataObservation(
    PacketProcessingDecision Decision,
    bool Consumed);
