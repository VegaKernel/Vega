using System.Text;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ConnectionMetadataRegistryTests
{
    [Test]
    public void MissingPlatformRemainsUnknownAndDirectEndpointsStaySeparate()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        ConnectionMetadataSnapshot snapshot = registry.Snapshot(
            7,
            "192.0.2.10:7777",
            TerrariaConnectionState.FullyConnected);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Platform, Is.EqualTo(ClientPlatformInfo.Unknown));
            Assert.That(snapshot.TransportEndpoint, Is.EqualTo("192.0.2.10:7777"));
            Assert.That(snapshot.EffectiveEndpoint, Is.EqualTo("192.0.2.10:7777"));
            Assert.That(snapshot.ForwardingProvenance, Is.EqualTo(ConnectionForwardingProvenance.Direct));
            Assert.That(snapshot.LoginPhase, Is.EqualTo(ConnectionLoginPhase.FullyConnected));
        });
    }

    [Test]
    public void PlatformPacketRequiresSenderSlotOwnership()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.PlayerPlatformInfo,
            new byte[] { 4, 233 },
            3,
            TerrariaConnectionState.SendingWorld);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo("connection_metadata.platform_sender_mismatch"));
            Assert.That(registry.Snapshot(3, null, TerrariaConnectionState.SendingWorld).Platform,
                Is.EqualTo(ClientPlatformInfo.Unknown));
        });
    }

    [TestCase(0, ClientPlatformKind.Mobile)]
    [TestCase(2, ClientPlatformKind.Xbox)]
    [TestCase(3, ClientPlatformKind.PlayStation)]
    [TestCase(5, ClientPlatformKind.Switch)]
    [TestCase(233, ClientPlatformKind.Pc)]
    [TestCase(99, ClientPlatformKind.Unrecognized)]
    public void PlatformPacketProjectsReportedValue(byte value, ClientPlatformKind expected)
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.PlayerPlatformInfo,
            new byte[] { 3, value },
            3,
            TerrariaConnectionState.SendingWorld);
        ClientPlatformInfo platform = registry
            .Snapshot(3, null, TerrariaConnectionState.FullyConnected)
            .Platform;

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(platform.Kind, Is.EqualTo(expected));
            Assert.That(platform.ReportedCode, Is.EqualTo(value));
        });
    }

    [Test]
    public void UuidTokenIsAcceptedOnceOnlyDuringProtocolAcceptedPhase()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        const string token = "755A3C87-6F2B-4EC9-AED4-B8EEAF855C81";
        byte[] payload = EncodeString(token);

        PacketProcessingDecision accepted = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            payload,
            9,
            TerrariaConnectionState.ProtocolAccepted);
        PacketProcessingDecision duplicate = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            payload,
            9,
            TerrariaConnectionState.ProtocolAccepted);
        string? uuid = registry
            .Snapshot(9, null, TerrariaConnectionState.ProtocolAccepted)
            .ClientUuid;

        Assert.Multiple(() =>
        {
            Assert.That(accepted.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(duplicate.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(uuid, Is.EqualTo(token), "ClientUUID is an opaque vanilla token and must not be normalized.");
        });
    }

    [TestCase("persisted-client-token")]
    [TestCase("755a3c876f2b4ec9aed4b8eeaf855c81")]
    [TestCase("client-uuid-with-non-guid-format")]
    [TestCase("клиент-uuid")]
    public void UuidAcceptsNonEmptyOpaqueVanillaTokens(string value)
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString(value),
            1,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(registry.Snapshot(1, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid,
                Is.EqualTo(value));
        });
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("\t\r\n")]
    public void UuidRejectsEmptyOrWhitespaceTokens(string value)
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString(value),
            1,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("connection_metadata.uuid_invalid"));
            Assert.That(registry.Snapshot(1, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid, Is.Null);
        });
    }

    [Test]
    public void UuidRejectsOverlongTruncatedAndTrailingPayloads()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        string overlong = new('x', 129);
        byte[] trailing = [.. EncodeString("persisted-token"), 0x7f];
        byte[] truncated = [5, (byte)'a'];

        PacketProcessingDecision overlongDecision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString(overlong),
            1,
            TerrariaConnectionState.ProtocolAccepted);
        PacketProcessingDecision trailingDecision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            trailing,
            2,
            TerrariaConnectionState.ProtocolAccepted);
        PacketProcessingDecision truncatedDecision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            truncated,
            3,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(overlongDecision.Reason, Is.EqualTo("connection_metadata.uuid_invalid"));
            Assert.That(trailingDecision.Reason, Is.EqualTo("connection_metadata.uuid_invalid"));
            Assert.That(truncatedDecision.Reason, Is.EqualTo("connection_metadata.uuid_invalid"));
            Assert.That(registry.Snapshot(1, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid, Is.Null);
            Assert.That(registry.Snapshot(2, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid, Is.Null);
            Assert.That(registry.Snapshot(3, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid, Is.Null);
        });
    }

    [Test]
    public void UuidRejectsWrongLoginPhase()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision late = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("persisted-client-token"),
            2,
            TerrariaConnectionState.FullyConnected);

        Assert.Multiple(() =>
        {
            Assert.That(late.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(late.Reason, Is.EqualTo("connection_metadata.uuid_wrong_phase"));
        });
    }

    [Test]
    public void ConnectRequestClearsUuidForReconnectSlotReuse()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision first = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("old-persisted-token"),
            4,
            TerrariaConnectionState.ProtocolAccepted);
        PacketProcessingDecision reset = registry.ObserveInbound(
            (byte)PacketTypes.ConnectRequest,
            [],
            4,
            TerrariaConnectionState.Connected);
        PacketProcessingDecision second = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("new-persisted-token"),
            4,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(first.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(reset.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(second.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(registry.Snapshot(4, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid,
                Is.EqualTo("new-persisted-token"));
        });
    }

    [Test]
    public void TrustedDimensionsPeerEstablishesEffectiveAddressAndConsumesPacket()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        registry.ConfigureDimensions(true, ["127.0.0.1"]);

        ConnectionMetadataObservation observation = registry.ObserveInboundDetailed(
            (byte)PacketTypes.Placeholder,
            EncodeDimensions(0, "203.0.113.9"),
            5,
            TerrariaConnectionState.ProtocolAccepted,
            "127.0.0.1:40000");
        ConnectionMetadataSnapshot snapshot = registry.Snapshot(
            5,
            "127.0.0.1:40000",
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(observation.Decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(observation.Consumed, Is.True);
            Assert.That(snapshot.TransportEndpoint, Is.EqualTo("127.0.0.1:40000"));
            Assert.That(snapshot.EffectiveEndpoint, Is.EqualTo("203.0.113.9"));
            Assert.That(snapshot.ForwardingProvenance, Is.EqualTo(ConnectionForwardingProvenance.TrustedForwarder));
        });
    }

    [Test]
    public void PublicClientCannotForgeDimensionsAddress()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        registry.ConfigureDimensions(true, ["127.0.0.1"]);

        ConnectionMetadataObservation observation = registry.ObserveInboundDetailed(
            (byte)PacketTypes.Placeholder,
            EncodeDimensions(0, "203.0.113.9"),
            5,
            TerrariaConnectionState.ProtocolAccepted,
            "198.51.100.2:40000");

        Assert.Multiple(() =>
        {
            Assert.That(observation.Decision.Reason, Is.EqualTo("connection_metadata.dimensions_untrusted_peer"));
            Assert.That(registry.Snapshot(5, "198.51.100.2:40000", TerrariaConnectionState.ProtocolAccepted)
                .EffectiveEndpoint, Is.EqualTo("198.51.100.2:40000"));
        });
    }

    [Test]
    public void SandboxBackendConsumesAdmissionAfterTrustedForwardingAndUuid()
    {
        var admission = new RecordingAdmissionService();
        var registry = new TerrariaConnectionMetadataRegistry();
        registry.ConfigureDimensions(true, ["127.0.0.1"]);
        registry.ConfigureSandboxAdmission(admission, new WorldId("arena"), required: true);

        ConnectionMetadataObservation forwarded = registry.ObserveInboundDetailed(
            (byte)PacketTypes.Placeholder,
            EncodeDimensions(0, "203.0.113.9"),
            5,
            TerrariaConnectionState.ProtocolAccepted,
            "127.0.0.1:40000");
        PacketProcessingDecision uuid = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("Persisted-Client-Token"),
            5,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(forwarded.Decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(uuid.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(admission.ConsumeCount, Is.EqualTo(1));
            Assert.That(admission.WorldId, Is.EqualTo(new WorldId("arena")));
            Assert.That(admission.EffectiveEndpoint, Is.EqualTo("203.0.113.9"));
            Assert.That(admission.ClientUuid, Is.EqualTo("Persisted-Client-Token"));
            Assert.That(registry.Snapshot(5, null, TerrariaConnectionState.ProtocolAccepted).TrustedTransferAccountId,
                Is.EqualTo("42"));
        });
    }

    [Test]
    public void PrimaryAllowsOrdinaryDimensionsJoinWhenNoTransferAdmissionExists()
    {
        var admission = new RecordingAdmissionService
        {
            ConsumeResult = new SandboxTransferAdmissionResult(
                false,
                "sandbox.transfer.admission_required",
                "No transfer is pending.")
        };
        var registry = new TerrariaConnectionMetadataRegistry();
        registry.ConfigureDimensions(true, ["127.0.0.1"]);
        registry.ConfigureSandboxAdmission(admission, WorldId.Primary, required: false);
        registry.ObserveInboundDetailed(
            (byte)PacketTypes.Placeholder,
            EncodeDimensions(0, "203.0.113.9"),
            5,
            TerrariaConnectionState.ProtocolAccepted,
            "127.0.0.1:40000");

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("persisted-client-token"),
            5,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(admission.ConsumeCount, Is.EqualTo(1));
            Assert.That(registry.Snapshot(5, null, TerrariaConnectionState.ProtocolAccepted).TrustedTransferAccountId, Is.Null);
        });
    }

    [Test]
    public void SandboxBackendRejectsUuidWithoutTrustedTransferAdmission()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        registry.ConfigureSandboxAdmission(new RecordingAdmissionService(), new WorldId("arena"), required: true);

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("persisted-client-token"),
            5,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("sandbox.transfer.admission_required"));
        });
    }

    private sealed class RecordingAdmissionService : ISandboxTransferAdmissionService
    {
        public SandboxTransferAdmissionResult? ConsumeResult { get; init; }
        public int ConsumeCount { get; private set; }
        public WorldId WorldId { get; private set; }
        public string? EffectiveEndpoint { get; private set; }
        public string? ClientUuid { get; private set; }

        public SandboxTransferAdmissionResult Register(
            SandboxTransferTicket ticket,
            string effectiveEndpoint,
            string clientUuid,
            DateTimeOffset now) => new(true);

        public SandboxTransferAdmissionResult Consume(
            WorldId receivingWorldId,
            string effectiveEndpoint,
            string clientUuid,
            DateTimeOffset now)
        {
            ConsumeCount++;
            WorldId = receivingWorldId;
            EffectiveEndpoint = effectiveEndpoint;
            ClientUuid = clientUuid;
            if (ConsumeResult is not null)
            {
                return ConsumeResult;
            }

            var ticket = new SandboxTransferTicket(
                Guid.NewGuid(),
                "42",
                WorldId.Primary,
                receivingWorldId,
                now,
                now + TimeSpan.FromSeconds(30),
                "nonce",
                "signature");
            return new SandboxTransferAdmissionResult(true, Ticket: ticket);
        }
    }

    private static byte[] EncodeString(string value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeDimensions(short type, string value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(type);
        writer.Write(value);
        writer.Flush();
        return stream.ToArray();
    }
}
