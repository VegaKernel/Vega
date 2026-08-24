using System.Text;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ClientUuidCompatibilityRegressionTests
{
    [Test]
    public void MaximumLengthOpaqueTokenIsAcceptedExactly()
    {
        var registry = new TerrariaConnectionMetadataRegistry();
        string token = new('u', 128);

        PacketProcessingDecision decision = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString(token),
            12,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(registry.Snapshot(12, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid,
                Is.EqualTo(token));
        });
    }

    [Test]
    public void DisconnectRemovalClearsOpaqueTokenBeforeSlotReuse()
    {
        var registry = new TerrariaConnectionMetadataRegistry();

        PacketProcessingDecision first = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("first-client-token"),
            12,
            TerrariaConnectionState.ProtocolAccepted);
        registry.Remove(12);
        PacketProcessingDecision second = registry.ObserveInbound(
            (byte)PacketTypes.ClientUUID,
            EncodeString("second-client-token"),
            12,
            TerrariaConnectionState.ProtocolAccepted);

        Assert.Multiple(() =>
        {
            Assert.That(first.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(second.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(registry.Snapshot(12, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid,
                Is.EqualTo("second-client-token"));
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
}
