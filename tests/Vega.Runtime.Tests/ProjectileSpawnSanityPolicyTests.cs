using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ProjectileSpawnSanityPolicyTests
{
    [Test]
    public void ValidProjectileFromSendingConnectionIsAllowed()
    {
        PacketProcessingDecision decision = Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 1000, 42),
            PositionX = 160f,
            PositionY = 320f,
            VelocityX = 1f,
            VelocityY = -1f,
            Type = 1
        });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void ForgedProjectileSpawnerIsRejected()
    {
        PacketProcessingDecision decision = Process(new ProjectileNew
        {
            Key = new ProjectileKey(8, 42, 0),
            Type = 1
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile.owner_mismatch")));
    }

    [Test]
    public void PackedProjectileIndexAboveProtocolLimitIsRejected()
    {
        var invalidKey = ProjectileKey.FromPacked((uint)(7 | (1001 << 8)));
        PacketProcessingDecision decision = Process(new ProjectileNew
        {
            Key = invalidKey,
            Type = 1
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile.index_out_of_range")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFinitePositionIsRejected(float value)
    {
        PacketProcessingDecision decision = Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            PositionX = value,
            Type = 1
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile.non_finite_position")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFiniteVelocityIsRejected(float value)
    {
        PacketProcessingDecision decision = Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            VelocityY = value,
            Type = 1
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile.non_finite_velocity")));
    }

    [Test]
    public void OutboundProjectileIsNotRestricted()
    {
        PacketProcessingDecision decision = Process(
            new ProjectileNew { Key = new ProjectileKey(8, 42, 0), Type = 1 },
            PacketDirection.Outbound);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void MultiplicityBoundaryRejectsSerializedForgedSpawner()
    {
        byte[] payload = Serialize(new ProjectileNew
        {
            Key = new ProjectileKey(8, 42, 0),
            Type = 1
        });

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.ProjectileNew,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            new ProjectileSpawnSanityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile.owner_mismatch")));
    }

    private static PacketProcessingDecision Process(
        ProjectileNew value,
        PacketDirection direction = PacketDirection.Inbound)
    {
        byte[] payload = Serialize(value);
        PacketView packet = PacketView.FromPayload(PacketTypes.ProjectileNew, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            direction,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);

        return new ProjectileSpawnSanityPolicy().Process(in context);
    }

    private static byte[] Serialize(ProjectileNew value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
