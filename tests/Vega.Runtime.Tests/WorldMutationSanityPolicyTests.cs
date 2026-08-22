using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class WorldMutationSanityPolicyTests
{
    [Test]
    public void TileOutsideWorldIsViolation() => AssertReason(
        new Tile { Action = TileAction.PlaceTile, TileX = 100, TileY = 10, Value = 1 },
        PacketTypes.Tile,
        "packet.tile.position_out_of_world");

    [Test]
    public void TileTypeOutsideRegistryIsViolation() => AssertReason(
        new Tile { Action = TileAction.PlaceTile, TileX = 10, TileY = 10, Value = 500 },
        PacketTypes.Tile,
        "packet.tile.type_out_of_range");

    [Test]
    public void ObjectTypeOutsideRegistryIsViolation() => AssertReason(
        new PlaceObject { X = 10, Y = 10, Type = 500 },
        PacketTypes.PlaceObject,
        "packet.object.type_out_of_range");

    [Test]
    public void DoorActionOutsideProtocolEnumIsViolation() => AssertReason(
        new DoorUse { Action = (DoorUseAction)99, TileX = 10, TileY = 10 },
        PacketTypes.DoorUse,
        "packet.door.action_out_of_range");

    [Test]
    public void PaintValueOutsideRegistryIsViolation() => AssertReason(
        new PaintTile { X = 10, Y = 10, Color = 32 },
        PacketTypes.PaintTile,
        "packet.paint.value_out_of_range");

    [Test]
    public void SignOutsideWorldIsViolation() => AssertReason(
        new SignNew { SignId = 1, X = 100, Y = 10, Text = "test", PlayerId = 7 },
        PacketTypes.SignNew,
        "packet.sign.position_out_of_world");

    [Test]
    public void ChestResizeAboveFortySlotsIsViolation() => AssertReason(
        new SyncChestSize { ChestId = 2, NewSize = 41 },
        PacketTypes.SyncChestSize,
        "packet.chest.size_out_of_range");

    [Test]
    public void PrefixAtRegistryCountIsViolation() => AssertReason(
        new PlaceItemFrame { X = 1, Y = 1, ItemId = 1, Prefix = 100, Stack = 1 },
        PacketTypes.PlaceItemFrame,
        "packet.item_frame.prefix_out_of_range");

    [Test]
    public void ChestItemSlotOutsideChestIsViolation() => AssertReason(
        new ChestItem { ChestId = 2, ItemSlot = 40, Stack = 1, ItemNetId = 1 },
        PacketTypes.ChestItem,
        "packet.chest.slot_out_of_range");

    [Test]
    public void LeashedAnchorOutsideWorldIsViolation() => AssertReason(
        new TELeashedEntityAnchorPlaceItem { TileX = -1, TileY = 10, ItemType = 1 },
        PacketTypes.TELeashedEntityAnchorPlaceItem,
        "packet.leashed_entity.anchor_out_of_world");

    [Test]
    public void LiquidOutsideWorldIsViolation() => AssertReason(
        new LiquidSet { X = 10, Y = 50, Liquid = 1 },
        PacketTypes.LiquidSet,
        "packet.liquid.position_out_of_world");

    [Test]
    public void TileEntityOutsideWorldIsViolation() => AssertReason(
        new PlaceTileEntity { X = -1, Y = 10 },
        PacketTypes.PlaceTileEntity,
        "packet.tile_entity.position_out_of_world");

    [Test]
    public void MassWireEndpointOutsideWorldIsViolation() => AssertReason(
        new MassWireOperation { StartX = 1, StartY = 1, EndX = 100, EndY = 2 },
        PacketTypes.MassWireOperation,
        "packet.mass_wire.position_out_of_world");

    [Test]
    public void QuickStackInventorySlotOutsideInventoryIsViolation() => AssertReason(
        new ForceItemIntoNearestChest { InventorySlot = 10 },
        PacketTypes.ForceItemIntoNearestChest,
        "packet.inventory.slot_out_of_range");

    [Test]
    public void PlayerChestIndexOutsideChestRegistryIsViolation() => AssertReason(
        new SyncPlayerChestIndex { Player = 7, Chest = 10 },
        PacketTypes.SyncPlayerChestIndex,
        "packet.chest.id_out_of_range");

    [Test]
    public void PersonalStorageChestIndicesRemainValid()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Process(
                new SyncPlayerChestIndex { Player = 7, Chest = -5 },
                PacketTypes.SyncPlayerChestIndex).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(Process(
                new SyncPlayerChestIndex { Player = 7, Chest = -6 },
                PacketTypes.SyncPlayerChestIndex).Reason,
                Is.EqualTo("packet.chest.id_out_of_range"));
        });
    }

    [Test]
    public void FoodPlatterItemTypeOutsideRegistryIsViolation() => AssertReason(
        new FoodPlatterTryPlacing { TileX = 1, TileY = 1, ItemType = 7000, Stack = 1 },
        PacketTypes.FoodPlatterTryPlacing,
        "packet.food_platter.item_type_out_of_range");

    [Test]
    public void DisplayJarOutsideWorldIsViolation() => AssertReason(
        new TEDeadCellsDisplayJar { TileX = 100, TileY = 1, ItemType = 1, Stack = 1 },
        PacketTypes.TEDeadCellsDisplayJar,
        "packet.display_jar.position_out_of_world");

    [Test]
    public void ValidBoundaryMutationsAreAllowed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Process(
                new Tile { Action = TileAction.PlaceWall, TileX = 99, TileY = 49, Value = 199 },
                PacketTypes.Tile).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(Process(
                new SyncChestSize { ChestId = 9, NewSize = 40 },
                PacketTypes.SyncChestSize).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    private static void AssertReason(TerrariaPacket value, PacketTypes packetType, string reason)
    {
        PacketProcessingDecision decision = Process(value, packetType);
        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo(reason));
        });
    }

    private static PacketProcessingDecision Process(TerrariaPacket value, PacketTypes packetType)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(packetType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        var policy = new WorldMutationSanityPolicy(
            () => 100,
            () => 50,
            () => 10,
            () => 10,
            tileTypeCount: 500,
            wallTypeCount: 200,
            itemTypeCount: 7_000,
            prefixCount: 100);

        return policy.Process(in context);
    }
}
