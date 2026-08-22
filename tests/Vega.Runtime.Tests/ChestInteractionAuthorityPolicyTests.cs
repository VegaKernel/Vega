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
        IReadOnlyDictionary<int, WorldTilePosition> chests) : IWorldInteractionState
    {
        public bool TryGetActiveChest(PlayerId playerId, out int chestId)
        {
            chestId = activeChest;
            return activeChest >= 0;
        }

        public bool TryGetChestPosition(int chestId, out WorldTilePosition position) =>
            chests.TryGetValue(chestId, out position);

        public bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position)
        {
            position = default;
            return false;
        }
    }
}
