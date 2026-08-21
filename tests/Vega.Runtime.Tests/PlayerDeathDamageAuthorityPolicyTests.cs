using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerDeathDamageAuthorityPolicyTests
{
    [TestCase(42_000, PacketDecisionKind.Continue)]
    [TestCase(42_001, PacketDecisionKind.Violation)]
    [TestCase(65_535, PacketDecisionKind.Violation)]
    public void InboundDamageIsComparedAsUnsigned(
        int unsignedDamage,
        PacketDecisionKind expected)
    {
        PacketProcessingDecision decision = Process(
            PacketDirection.Inbound,
            CreatePlayerDeathPayload(unsignedDamage));

        Assert.That(decision.Kind, Is.EqualTo(expected));
        if (expected == PacketDecisionKind.Violation)
        {
            Assert.That(
                decision.Reason,
                Is.EqualTo("packet.player_death.damage_out_of_range"));
        }
    }

    [Test]
    public void OutboundDeathSynchronizationIsNotRejected()
    {
        PacketProcessingDecision decision = Process(
            PacketDirection.Outbound,
            CreatePlayerDeathPayload(65_535));

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void UnrelatedInboundPacketIsIgnored()
    {
        PacketProcessingDecision decision = Process(
            PacketDirection.Inbound,
            packetType: PacketTypes.PlayerActive,
            payload: [7, 1]);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void MultiplicityBoundaryRejectsSerializedExploitValue()
    {
        byte[] payload = CreatePlayerDeathPayload(65_535);

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerDeathV2,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            null,
            new PlayerDeathDamageAuthorityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
    }

    private static PacketProcessingDecision Process(
        PacketDirection direction,
        byte[] payload) =>
        Process(direction, PacketTypes.PlayerDeathV2, payload);

    private static PacketProcessingDecision Process(
        PacketDirection direction,
        PacketTypes packetType,
        byte[] payload)
    {
        var packet = PacketView.FromPayload(packetType, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            direction,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        var policy = new PlayerDeathDamageAuthorityPolicy();

        return policy.Process(in context);
    }

    private static byte[] CreatePlayerDeathPayload(int unsignedDamage)
    {
        var packet = new PlayerDeathV2
        {
            PlayerId = 7,
            Damage = unchecked((short)(ushort)unsignedDamage)
        };
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
