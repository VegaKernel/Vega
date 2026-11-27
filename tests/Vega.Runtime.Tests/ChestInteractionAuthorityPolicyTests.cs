using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ChestInteractionAuthorityPolicyTests
{
    [Test]
    public void ChestItemMustTargetPlayersActiveChest()
    {
        var world = new FakeWorld(activeChest: 2, new Dictionary<int, WorldTilePosition>
        {
            [2] = new(10, 20),
            [3] = new(30, 40)
        });
        var policy = new ChestInteractionAuthorityPolicy(world);

        PacketProcessingDecision valid = Process(policy,
            new ChestItem { ChestId = 2, ItemSlot = 1, ItemNetId = 1 });
        PacketProcessingDecision spoofed = Process(policy,
            new ChestItem { ChestId = 3, ItemSlot = 1, ItemNetId = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(valid.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(spoofed.Reason, Is.EqualTo("packet.chest.session_mismatch"));
        });
    }

    [TestCase(40)]
    [TestCase(80)]
    [TestCase(200)]
    public void CanonicalChestUsesOwnCapacityAndDoesNotPermitResize(int capacity)
    {
        var world = new FakeWorld(2, new Dictionary<int, WorldTilePosition> { [2] = new(10, 20) }, capacity);
        var policy = new ChestInteractionAuthorityPolicy(world);
        var player = new PlayerId(4);
        Assert.That(policy.ValidateChestItem(player, 2, capacity - 1).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(policy.ValidateChestItem(player, 2, capacity).Kind, Is.EqualTo(PacketDecisionKind.Reject));
        Assert.That(policy.ValidateChestSize(player, 2, capacity).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(policy.ValidateChestSize(player, 2, capacity == 40 ? 80 : 40).Kind, Is.EqualTo(PacketDecisionKind.Reject));
        Assert.That(policy.ValidateChestItem(null, 2, 0).Kind, Is.EqualTo(PacketDecisionKind.Reject));
    }

    [TestCase(null)]
    [TestCase(0)]
    [TestCase(201)]
    public void UnavailableCapacityFailsClosed(int? capacity)
    {
        var policy = new ChestInteractionAuthorityPolicy(new FakeWorld(2, new Dictionary<int, WorldTilePosition> { [2] = new(10, 20) }, capacity));
        Assert.That(policy.ValidateChestItem(new PlayerId(4), 2, 0).Reason, Is.EqualTo("packet.chest.capacity_unavailable"));
    }

    [Test]
    public void ChestOpenCoordinatesMustMatchAuthoritativeChest()
    {
        var policy = new ChestInteractionAuthorityPolicy(new FakeWorld(
            activeChest: -1,
            new Dictionary<int, WorldTilePosition> { [2] = new(10, 20) }));

        PacketProcessingDecision valid = Process(policy,
            new ChestOpen { ChestId = 2, ChestX = 10, ChestY = 20 });
        PacketProcessingDecision spoofed = Process(policy,
            new ChestOpen { ChestId = 2, ChestX = 11, ChestY = 20 });
        PacketProcessingDecision close = Process(policy,
            new ChestOpen { ChestId = -1 });

        Assert.Multiple(() =>
        {
            Assert.That(valid.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(spoofed.Reason, Is.EqualTo("packet.chest.position_mismatch"));
            Assert.That(close.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    private static PacketProcessingDecision Process(
        ChestInteractionAuthorityPolicy policy,
        TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(4),
            new PlayerId(4),
            DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class FakeWorld(
        int activeChest,
        IReadOnlyDictionary<int, WorldTilePosition> chests, int? capacity = 40) : IWorldInteractionState
    {
        public bool TryGetActiveChest(PlayerId playerId, out int chestId)
        {
            chestId = activeChest;
            return activeChest >= 0;
        }

        public bool TryGetChestPosition(int chestId, out WorldTilePosition position) =>
            chests.TryGetValue(chestId, out position);

        public bool TryGetChestCapacity(int chestId, out int result)
        {
            result = capacity.GetValueOrDefault();
            return capacity.HasValue && chests.ContainsKey(chestId);
        }

        public bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position)
        {
            position = default;
            return false;
        }
    }
}
