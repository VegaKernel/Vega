using System.Buffers.Binary;
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
    public void ValidInboundNpcCoinPickupRequestPassesProtocolSanity()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncExtraValue,
            PacketDirection.Inbound,
            CreateSyncExtraValuePayload());

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void NegativeNpcIndexIsRejectedBeforeLiveArrayLookup()
    {
        byte[] payload = CreateSyncExtraValuePayload(npcIndex: -1);

        Assert.That(
            Process(PacketTypes.SyncExtraValue, PacketDirection.Inbound, payload),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.npc_coin_pickup.npc_out_of_range")));
    }

    [Test]
    public void NonFiniteCoinPositionIsRejected()
    {
        byte[] payload = CreateSyncExtraValuePayload(x: float.NaN);

        Assert.That(
            Process(PacketTypes.SyncExtraValue, PacketDirection.Inbound, payload),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.npc_coin_pickup.non_finite_position")));
    }

    [Test]
    public void OutboundNpcCoinPickupUpdateRemainsAvailableToServer()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncExtraValue,
            PacketDirection.Outbound,
            CreateSyncExtraValuePayload());

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void UnrelatedInboundPacketIsIgnored()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.PlayerActive,
            PacketDirection.Inbound,
            [7, 1]);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MultiplicityBoundaryAcceptsProtocol326Int32ExtraValueLayout()
    {
        byte[] payload = CreateSyncExtraValuePayload(extraValue: 70);

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.SyncExtraValue,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            null,
            new NpcCoinPickupAuthorityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
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

    private static byte[] CreateSyncExtraValuePayload(
        short npcIndex = 42,
        int extraValue = 70,
        float x = 160,
        float y = 320)
    {
        byte[] payload = new byte[14];
        BinaryPrimitives.WriteInt16LittleEndian(payload, npcIndex);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), extraValue);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(10), BitConverter.SingleToInt32Bits(y));
        return payload;
    }
}
