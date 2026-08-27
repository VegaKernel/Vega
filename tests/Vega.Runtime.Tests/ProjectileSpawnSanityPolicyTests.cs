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

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void ForgedProjectileSpawnerIsViolation()
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(8, 42, 0),
            Type = 1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.owner_mismatch")));
    }

    [Test]
    public void PackedProjectileIndexAboveRuntimeLimitIsViolation()
    {
        var invalidKey = ProjectileKey.FromPacked((uint)(7 | (1001 << 8)));
        Assert.That(Process(new ProjectileNew
        {
            Key = invalidKey,
            Type = 1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.index_out_of_range")));
    }

    [Test]
    public void ProjectileTypeOutsideRegistryIsViolation()
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            Type = 2000
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.type_out_of_range")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFinitePositionIsViolation(float value)
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            PositionX = value,
            Type = 1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.non_finite_position")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFiniteVelocityIsViolation(float value)
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            VelocityY = value,
            Type = 1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.non_finite_velocity")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void NonFiniteAiIsViolation(float value)
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            Type = 1,
            AI1 = value
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.non_finite_ai")));
    }

    [Test]
    public void NegativeClientAuthoredDamageIsViolation()
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            Type = 1,
            Damage = -1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.negative_damage")));
    }

    [Test]
    public void NegativeClientAuthoredOriginalDamageIsViolation()
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            Type = 1,
            OriginalDamage = -1
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.negative_original_damage")));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    [TestCase(-0.01f)]
    public void InvalidClientAuthoredKnockbackIsViolation(float value)
    {
        Assert.That(Process(new ProjectileNew
        {
            Key = new ProjectileKey(7, 42, 0),
            Type = 1,
            KnockBack = value
        }), Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.invalid_knockback")));
    }

    [Test]
    public void DestroyValidatesOwnerIndexAndPosition()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Process(new ProjectileDestroy
            {
                Key = new ProjectileKey(8, 42, 0)
            }, PacketTypes.ProjectileDestroy),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.owner_mismatch")));
            Assert.That(Process(new ProjectileDestroy
            {
                Key = new ProjectileKey(7, 42, 0),
                PositionX = float.NaN
            }, PacketTypes.ProjectileDestroy),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.non_finite_position")));
        });
    }

    [Test]
    public void OutboundProjectileIsNotRestricted()
    {
        PacketProcessingDecision decision = Process(
            new ProjectileNew { Key = new ProjectileKey(8, 42, 0), Type = 1 },
            PacketTypes.ProjectileNew,
            PacketDirection.Outbound);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
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
            new ProjectileSpawnSanityPolicy(projectileCount: 1001, projectileTypeCount: 2000),
            DateTimeOffset.UtcNow);

        Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.owner_mismatch")));
    }

    private static PacketProcessingDecision Process(
        TerrariaPacket value,
        PacketTypes packetType = PacketTypes.ProjectileNew,
        PacketDirection direction = PacketDirection.Inbound)
    {
        byte[] payload = Serialize(value);
        PacketView packet = PacketView.FromPayload(packetType, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            direction,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);

        return new ProjectileSpawnSanityPolicy(projectileCount: 1001, projectileTypeCount: 2000).Process(in context);
    }

    private static byte[] Serialize(TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
