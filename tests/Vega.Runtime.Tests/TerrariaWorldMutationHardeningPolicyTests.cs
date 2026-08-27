using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaWorldMutationHardeningPolicyTests
{
    [Test]
    public void DoorUseRequiresALiveDoorTile()
    {
        const int x = 10;
        const int y = 20;
        ushort currentTileType = global::Terraria.ID.TileID.Dirt;
        var policy = CreatePolicy((_, _) => currentTileType);
        PacketProcessingDecision nonDoor = Process(policy, new DoorUse
        {
            Action = (DoorUseAction)0,
            TileX = x,
            TileY = y
        });

        currentTileType = global::Terraria.ID.TileID.ClosedDoor;
        PacketProcessingDecision door = Process(policy, new DoorUse
        {
            Action = (DoorUseAction)0,
            TileX = x,
            TileY = y
        });

        Assert.Multiple(() =>
        {
            Assert.That(nonDoor, Is.EqualTo(PacketProcessingDecision.Reject("packet.door.tile_type_mismatch")));
            Assert.That(door.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    [Test]
    public void ChestItemStackCannotExceedNativeItemLimit()
    {
        PacketProcessingDecision decision = Process(CreatePolicy(itemMaxStack: _ => 1), new ChestItem
        {
            ChestId = 0,
            ItemSlot = 0,
            Stack = 2,
            ItemNetId = 50
        });

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.chest.stack_exceeds_item_limit")));
    }

    [Test]
    public void PlaceItemFrameRequiresLiveNativeFrame()
    {
        PacketProcessingDecision missing = Process(
            CreatePolicy(itemFrameExists: static (_, _) => false),
            new PlaceItemFrame { X = 10, Y = 20, ItemId = 1, Prefix = 0, Stack = 1 });
        PacketProcessingDecision existing = Process(
            CreatePolicy(itemFrameExists: static (_, _) => true),
            new PlaceItemFrame { X = 10, Y = 20, ItemId = 1, Prefix = 0, Stack = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.EqualTo(PacketProcessingDecision.Reject("packet.item_frame.not_found")));
            Assert.That(existing.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    private static TerrariaWorldMutationHardeningPolicy CreatePolicy(
        Func<int, int, ushort>? tileType = null,
        Func<int, int>? itemMaxStack = null,
        Func<int, int, bool>? itemFrameExists = null) => new(
            static () => 100,
            static () => 100,
            tileType ?? (static (_, _) => global::Terraria.ID.TileID.ClosedDoor),
            itemMaxStack ?? (static _ => 9999),
            itemFrameExists ?? (static (_, _) => true));

    private static PacketProcessingDecision Process(
        TerrariaWorldMutationHardeningPolicy policy,
        TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);

        return policy.Process(in context);
    }
}
