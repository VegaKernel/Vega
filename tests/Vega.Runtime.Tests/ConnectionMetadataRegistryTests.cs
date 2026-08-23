using System.Text;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
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
    public void UuidIsAcceptedOnceOnlyDuringProtocolAcceptedPhase()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        byte[] payload = EncodeString("755A3C87-6F2B-4EC9-AED4-B8EEAF855C81");

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
            Assert.That(uuid, Is.EqualTo("755a3c87-6f2b-4ec9-aed4-b8eeaf855c81"));
        });
    }

    [TestCase("")]
    [TestCase("not-a-uuid")]
    [TestCase("755a3c876f2b4ec9aed4b8eeaf855c81")]
    [TestCase("755a3c87-6f2b-4ec9-aed4-b8eeaf855c8z")]
    public void UuidRejectsEmptyAndNonCanonicalValues(string value)
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
    public void UuidRejectsMalformedValueAndWrongLoginPhase()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision malformed = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("not-a-uuid"),
            1,
            TerrariaConnectionState.ProtocolAccepted);
        PacketProcessingDecision late = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("755a3c87-6f2b-4ec9-aed4-b8eeaf855c81"),
            2,
            TerrariaConnectionState.FullyConnected);

        Assert.Multiple(() =>
        {
            Assert.That(malformed.Reason, Is.EqualTo("connection_metadata.uuid_invalid"));
            Assert.That(late.Reason, Is.EqualTo("connection_metadata.uuid_wrong_phase"));
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
