using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using TShockNG.Contracts.Networking;
using Vega.Core.Networking;
using WirePacketTypes = Multiplicity.Packets.PacketTypes;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerPacketOwnershipPolicyTests
{
    [TestCase(WirePacketTypes.PlayerHp)]
    [TestCase(WirePacketTypes.PlayerMana)]
    [TestCase(WirePacketTypes.PlayerTeam)]
    [TestCase(WirePacketTypes.TogglePvp)]
    public void MatchingConnectionSlotIsAllowed(WirePacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 7);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [TestCase(WirePacketTypes.PlayerHp)]
    [TestCase(WirePacketTypes.PlayerMana)]
    [TestCase(WirePacketTypes.PlayerTeam)]
    [TestCase(WirePacketTypes.TogglePvp)]
    public void ForeignPlayerSlotIsRejected(WirePacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 8);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_identity_mismatch"));
        });
    }

    [Test]
    public void UnrelatedPacketIsIgnored()
    {
        var packet = PacketView.FromPayload(WirePacketTypes.PlayerActive, new byte[] { 8, 1 });
        var context = Context(packet, connectionSlot: 7);
        var policy = new PlayerPacketOwnershipPolicy();

        PacketProcessingDecision decision = policy.Process(in context);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    private static PacketProcessingDecision Process(
        WirePacketTypes packetType,
        int connectionSlot,
        byte claimedSlot)
    {
        byte[] payload = packetType switch
        {
            WirePacketTypes.PlayerHp or WirePacketTypes.PlayerMana => [claimedSlot, 100, 0, 120, 0],
            WirePacketTypes.PlayerTeam => [claimedSlot, 1],
            WirePacketTypes.TogglePvp => [claimedSlot, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(packetType))
        };
        var packet = PacketView.FromPayload(packetType, payload);
        var context = Context(packet, connectionSlot);
        var policy = new PlayerPacketOwnershipPolicy();

        return policy.Process(in context);
    }

    private static PacketProcessingContext<PacketView> Context(
        PacketView packet,
        int connectionSlot) =>
        new(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(connectionSlot),
            null,
            DateTimeOffset.UtcNow);
}
