using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NpcCoinPickupAuthorityPolicyTests
{
    [Test]
    public void InboundNpcCoinPickupUpdateIsRejectedWithoutReadingTerrariaState()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncExtraValue,
            PacketDirection.Inbound,
            CreateSyncExtraValuePayload());

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(
                decision.Reason,
                Is.EqualTo("packet.npc_coin_pickup_server_authoritative"));
        });
    }

    [Test]
    public void OutboundNpcCoinPickupUpdateRemainsAvailableToServer()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncExtraValue,
            PacketDirection.Outbound,
            CreateSyncExtraValuePayload());

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void UnrelatedInboundPacketIsIgnored()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.PlayerActive,
            PacketDirection.Inbound,
            [7, 1]);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void MultiplicityBoundaryParsesAndRejectsRealPacketPayload()
    {
        byte[] payload = CreateSyncExtraValuePayload();

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.SyncExtraValue,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            null,
            new NpcCoinPickupAuthorityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
    }

    private static PacketProcessingDecision Process(
        PacketTypes packetType,
        PacketDirection direction,
        byte[] payload)
    {
        var packet = PacketView.FromPayload(packetType, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            direction,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        var policy = new NpcCoinPickupAuthorityPolicy();

        return policy.Process(in context);
    }

    private static byte[] CreateSyncExtraValuePayload()
    {
        var packet = new SyncExtraValue
        {
            NpcIndex = 42,
            ExtraValue = 70,
            X = 160,
            Y = 320
        };
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
