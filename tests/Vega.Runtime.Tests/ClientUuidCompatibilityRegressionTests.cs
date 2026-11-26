using System.Text;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using TerrariaRuntime;
using Vega.Contracts.Networking;
using Vega.Core.Networking;
using Vega.Operations.Logging;
using Vega.Operations.Metrics;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ClientUuidCompatibilityRegressionTests
{
    [Test]
    [NonParallelizable]
    public void ReceiveHookAcceptsUuidWithoutReadingIntoNextFrame()
    {
        const int slot = 12;
        string token = new('u', 128);
        var registry = new TerrariaConnectionMetadataRegistry();
        using var hooks = new NativePlayerPacketSecurityHooks(
            new PacketProcessorPipeline<PacketView>([]), registry,
            new StructuredOperationsLogger([]), new NetworkMetricsCounter(), new BoundedSecuritySignalStore());
        byte[] payload = EncodeString(token);
        var buffer = new global::Terraria.MessageBuffer { whoAmI = slot };
        byte packetId = (byte)PacketTypes.ClientUUID;
        int start = 7;
        int readOffset = start + 1;
        int length = payload.Length + 1;
        int messageType = packetId;
        buffer.readBuffer[start] = packetId;
        payload.CopyTo(buffer.readBuffer, readOffset);
        buffer.readBuffer[start + length] = 3;
        buffer.readBuffer[start + length + 2] = global::Terraria.ID.MessageID.RequestWorldData;

        var previousClient = global::Terraria.Netplay.Clients[slot];
        global::Terraria.Netplay.Clients[slot] = new global::Terraria.RemoteClient
        {
            State = (int)TerrariaConnectionState.ProtocolAccepted
        };
        try
        {
            hooks.Attach();
            bool allowed = Hooks.MessageBuffer.InvokeGetData(
                buffer, ref packetId, ref readOffset, ref start, ref length, ref messageType, 256);

            Assert.Multiple(() =>
            {
                Assert.That(allowed, Is.True);
                Assert.That(registry.Snapshot(slot, null, TerrariaConnectionState.ProtocolAccepted).ClientUuid,
                    Is.EqualTo(token));
            });
        }
        finally
        {
            global::Terraria.Netplay.Clients[slot] = previousClient;
        }
    }

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
