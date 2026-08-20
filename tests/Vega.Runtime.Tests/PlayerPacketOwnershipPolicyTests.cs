using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerPacketOwnershipPolicyTests
{
    [TestCase(PacketTypes.PlayerHp)]
    [TestCase(PacketTypes.PlayerMana)]
    [TestCase(PacketTypes.PlayerTeam)]
    [TestCase(PacketTypes.TogglePvp)]
    [TestCase(PacketTypes.SpectatePlayer)]
    [TestCase(PacketTypes.PlayerDeathV2)]
    public void MatchingConnectionSlotIsAllowed(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 7);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [TestCase(PacketTypes.PlayerHp)]
    [TestCase(PacketTypes.PlayerMana)]
    [TestCase(PacketTypes.PlayerTeam)]
    [TestCase(PacketTypes.TogglePvp)]
    [TestCase(PacketTypes.SpectatePlayer)]
    [TestCase(PacketTypes.PlayerDeathV2)]
    public void ForeignPlayerSlotIsRejected(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 8);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_identity_mismatch"));
        });
    }

    [Test]
    public void OutboundPlayerStateIsNotTreatedAsAClientForgery()
    {
        var packet = PacketView.FromPayload(
            PacketTypes.PlayerDeathV2,
            CreatePlayerDeathPayload(claimedSlot: 8));
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Outbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        var policy = new PlayerPacketOwnershipPolicy();

        PacketProcessingDecision decision = policy.Process(in context);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void UnrelatedPacketIsIgnored()
    {
        var packet = PacketView.FromPayload(PacketTypes.PlayerActive, new byte[] { 8, 1 });
        var context = Context(packet, connectionSlot: 7);
        var policy = new PlayerPacketOwnershipPolicy();

        PacketProcessingDecision decision = policy.Process(in context);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    private static PacketProcessingDecision Process(
        PacketTypes packetType,
        int connectionSlot,
        byte claimedSlot)
    {
        byte[] payload = packetType switch
        {
            PacketTypes.PlayerHp or PacketTypes.PlayerMana => [claimedSlot, 100, 0, 120, 0],
            PacketTypes.PlayerTeam => [claimedSlot, 1],
            PacketTypes.TogglePvp => [claimedSlot, 1],
            PacketTypes.SpectatePlayer => [claimedSlot, 2, 0],
            PacketTypes.PlayerDeathV2 => CreatePlayerDeathPayload(claimedSlot),
            _ => throw new ArgumentOutOfRangeException(nameof(packetType))
        };
        var packet = PacketView.FromPayload(packetType, payload);
        var context = Context(packet, connectionSlot);
        var policy = new PlayerPacketOwnershipPolicy();

        return policy.Process(in context);
    }

    private static byte[] CreatePlayerDeathPayload(byte claimedSlot)
    {
        var packet = new PlayerDeathV2
        {
            PlayerId = claimedSlot,
            Damage = 100
        };
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
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
