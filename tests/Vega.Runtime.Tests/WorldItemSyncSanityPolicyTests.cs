using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class WorldItemSyncSanityPolicyTests
{
    [TestCase(PacketTypes.SyncItemsWithShimmer)]
    [TestCase(PacketTypes.SyncItemCannotBeTakenByEnemies)]
    public void ValidBoundaryPayloadIsAllowed(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(
            packetType,
            PacketDirection.Inbound,
            CreatePayload(packetType, itemIndex: 400, stack: 9_999));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [TestCase(PacketTypes.SyncItemsWithShimmer, -1)]
    [TestCase(PacketTypes.SyncItemCannotBeTakenByEnemies, 401)]
    public void InvalidItemIndexIsRejected(PacketTypes packetType, short itemIndex)
    {
        PacketProcessingDecision decision = Process(
            packetType,
            PacketDirection.Inbound,
            CreatePayload(packetType, itemIndex: itemIndex));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.world_item.index_out_of_range"));
        });
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFiniteTransformIsRejected(float coordinate)
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncItemsWithShimmer,
            PacketDirection.Inbound,
            CreatePayload(PacketTypes.SyncItemsWithShimmer, positionX: coordinate));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.world_item.non_finite_transform"));
        });
    }

    [TestCase(-1)]
    [TestCase(10_000)]
    public void LegacyStackOutsideAcceptedRangeIsRejected(short stack)
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncItemCannotBeTakenByEnemies,
            PacketDirection.Inbound,
            CreatePayload(PacketTypes.SyncItemCannotBeTakenByEnemies, stack: stack));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.world_item.stack_out_of_range"));
        });
    }

    [Test]
    public void OutboundPayloadIsNotRestricted()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.SyncItemsWithShimmer,
            PacketDirection.Outbound,
            CreatePayload(PacketTypes.SyncItemsWithShimmer, itemIndex: -1));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void UnrelatedWorldItemPacketIsNotClaimed()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.ItemDrop,
            PacketDirection.Inbound,
            CreatePayload(PacketTypes.ItemDrop, itemIndex: -1));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void MultiplicityBoundaryRejectsSerializedExploitPayload()
    {
        byte[] payload = CreatePayload(
            PacketTypes.SyncItemsWithShimmer,
            velocityY: float.PositiveInfinity);

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.SyncItemsWithShimmer,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            null,
            new WorldItemSyncSanityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
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
        var policy = new WorldItemSyncSanityPolicy();

        return policy.Process(in context);
    }

    private static byte[] CreatePayload(
        PacketTypes packetType,
        short itemIndex = 10,
        float positionX = 160,
        float positionY = 320,
        float velocityX = 1,
        float velocityY = -1,
        short stack = 1)
    {
        WorldItemSyncPacketBase packet = packetType switch
        {
            PacketTypes.ItemDrop => new ItemDrop(),
            PacketTypes.SyncItemsWithShimmer => new SyncItemsWithShimmer(),
            PacketTypes.SyncItemCannotBeTakenByEnemies => new SyncItemCannotBeTakenByEnemies(),
            _ => throw new ArgumentOutOfRangeException(nameof(packetType))
        };

        packet.ItemIndex = itemIndex;
        packet.PositionX = positionX;
        packet.PositionY = positionY;
        packet.VelocityX = velocityX;
        packet.VelocityY = velocityY;
        packet.Stack = stack;
        packet.ItemNetId = 1;

        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
