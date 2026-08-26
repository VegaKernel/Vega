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
    [TestCase(PacketTypes.ItemDrop)]
    [TestCase(PacketTypes.UpdateItemDrop)]
    public void ValidBoundaryPayloadIsAllowed(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(
            packetType,
            PacketDirection.Inbound,
            CreateWorldItemPayload(packetType, itemIndex: 400, stack: 9_999));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [TestCase(PacketTypes.ItemDrop, -1)]
    [TestCase(PacketTypes.UpdateItemDrop, 401)]
    public void InvalidItemIndexIsViolation(PacketTypes packetType, short itemIndex)
    {
        AssertViolation(
            packetType,
            CreateWorldItemPayload(packetType, itemIndex: itemIndex),
            "packet.world_item.index_out_of_range");
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFiniteTransformIsViolation(float coordinate)
    {
        AssertViolation(
            PacketTypes.ItemDrop,
            CreateWorldItemPayload(PacketTypes.ItemDrop, positionX: coordinate),
            "packet.world_item.non_finite_transform");
    }

    [TestCase(-1)]
    [TestCase(10_000)]
    public void StackOutsideAcceptedRangeIsViolation(short stack)
    {
        AssertViolation(
            PacketTypes.UpdateItemDrop,
            CreateWorldItemPayload(PacketTypes.UpdateItemDrop, stack: stack),
            "packet.world_item.stack_out_of_range");
    }

    [TestCase(PacketTypes.SyncItemsWithShimmerDeprecated)]
    [TestCase(PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated)]
    public void Protocol326DeprecatedItemPacketsAreViolation(PacketTypes packetType)
    {
        AssertViolation(
            packetType,
            CreateWorldItemPayload(PacketTypes.ItemDrop),
            "packet.world_item.deprecated_packet");
    }

    [TestCase(-49)]
    [TestCase(7000)]
    public void ItemTypeOutsideRegistryIsViolation(short itemNetId)
    {
        AssertViolation(
            PacketTypes.ItemDrop,
            CreateWorldItemPayload(PacketTypes.ItemDrop, itemNetId: itemNetId),
            "packet.world_item.type_out_of_range");
    }

    [Test]
    public void PrefixOutsideRegistryIsViolation()
    {
        AssertViolation(
            PacketTypes.ItemDrop,
            CreateWorldItemPayload(PacketTypes.ItemDrop, prefix: 100),
            "packet.world_item.prefix_out_of_range");
    }

    [Test]
    public void DespawnIndexIsValidated()
    {
        AssertViolation(
            PacketTypes.SyncItemDespawn,
            Serialize(new SyncItemDespawn { ItemId = 401 }),
            "packet.world_item.index_out_of_range");
    }

    [Test]
    public void PositionSyncRequiresFiniteCoordinates()
    {
        AssertViolation(
            PacketTypes.SyncItemPosition,
            Serialize(new SyncItemPosition { ItemId = 10, PositionX = float.NaN, PositionY = 10 }),
            "packet.world_item.non_finite_position");
    }

    [Test]
    public void OutboundPayloadIsNotRestricted()
    {
        PacketProcessingDecision decision = Process(
            PacketTypes.ItemDrop,
            PacketDirection.Outbound,
            CreateWorldItemPayload(PacketTypes.ItemDrop, itemIndex: -1));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void UnrelatedPacketIsNotClaimed()
    {
        PacketProcessingDecision decision = Process(PacketTypes.ItemOwner, PacketDirection.Inbound, []);
        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MultiplicityBoundaryRejectsSerializedExploitPayload()
    {
        byte[] payload = CreateWorldItemPayload(PacketTypes.ItemDrop, velocityY: float.PositiveInfinity);

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.ItemDrop,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            null,
            new WorldItemSyncSanityPolicy(itemTypeCount: 7000, prefixCount: 100),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
    }

    private static void AssertViolation(PacketTypes packetType, byte[] payload, string reason)
    {
        PacketProcessingDecision decision = Process(packetType, PacketDirection.Inbound, payload);
        Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Violation(reason)));
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
        var policy = new WorldItemSyncSanityPolicy(itemTypeCount: 7000, prefixCount: 100);

        return policy.Process(in context);
    }

    private static byte[] CreateWorldItemPayload(
        PacketTypes packetType,
        short itemIndex = 10,
        float positionX = 160,
        float positionY = 320,
        float velocityX = 1,
        float velocityY = -1,
        short stack = 1,
        byte prefix = 0,
        short itemNetId = 1)
    {
        WorldItemSyncPacketBase packet = packetType switch
        {
            PacketTypes.ItemDrop => new ItemDrop(),
            PacketTypes.UpdateItemDrop => new UpdateItemDrop(),
            _ => throw new ArgumentOutOfRangeException(nameof(packetType))
        };

        packet.ItemIndex = itemIndex;
        packet.PositionX = positionX;
        packet.PositionY = positionY;
        packet.VelocityX = velocityX;
        packet.VelocityY = velocityY;
        packet.Stack = stack;
        packet.Prefix = prefix;
        packet.ItemNetId = itemNetId;
        return Serialize(packet);
    }

    private static byte[] Serialize(TerrariaPacket packet)
    {
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
