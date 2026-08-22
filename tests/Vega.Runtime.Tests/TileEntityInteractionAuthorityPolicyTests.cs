using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TileEntityInteractionAuthorityPolicyTests
{
    [Test]
    public void UnknownTileEntityIsRejectedBeforeTerrariaUsesIt()
    {
        var policy = new TileEntityInteractionAuthorityPolicy(new FakeWorld());

        PacketProcessingDecision decision = Process(policy,
            new RequestTileEntityInteraction { TileEntityId = 42, PlayerId = 4 });

        Assert.That(decision.Reason, Is.EqualTo("packet.tile_entity.not_found"));
    }

    private static PacketProcessingDecision Process(
        TileEntityInteractionAuthorityPolicy policy,
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

    private sealed class FakeWorld : IWorldInteractionState
    {
        public bool TryGetActiveChest(PlayerId playerId, out int chestId)
        {
            chestId = -1;
            return false;
        }

        public bool TryGetChestPosition(int chestId, out WorldTilePosition position)
        {
            position = default;
            return false;
        }

        public bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position)
        {
            position = default;
            return false;
        }
    }
}
