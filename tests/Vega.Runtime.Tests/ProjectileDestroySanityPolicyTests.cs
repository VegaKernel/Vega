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
public sealed class ProjectileDestroySanityPolicyTests
{
    [Test]
    public void SendingConnectionCanDestroyOwnProjectile()
    {
        PacketProcessingDecision decision = Process(new ProjectileDestroy
        {
            Key = new ProjectileKey(7, 1000, 42),
            PositionX = 160f,
            PositionY = 320f
        });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void ForeignProjectileSpawnerIsViolation()
    {
        PacketProcessingDecision decision = Process(new ProjectileDestroy
        {
            Key = new ProjectileKey(8, 42, 0)
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile_destroy.owner_mismatch")));
    }

    [Test]
    public void PackedProjectileIndexAboveProtocolLimitIsViolation()
    {
        var invalidKey = ProjectileKey.FromPacked((uint)(7 | (1001 << 8)));
        PacketProcessingDecision decision = Process(new ProjectileDestroy
        {
            Key = invalidKey
        });

        Assert.That(decision, Is.EqualTo(
            new PacketProcessingDecision(PacketDecisionKind.Violation, "packet.projectile_destroy.index_out_of_range")));
    }

    [Test]
    public void OutboundProjectileDestroyIsNotRestricted()
    {
        PacketProcessingDecision decision = Process(
            new ProjectileDestroy { Key = new ProjectileKey(8, 42, 0) },
            PacketDirection.Outbound);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void MultiplicityBoundaryRejectsSerializedForeignProjectile()
    {
        byte[] payload = Serialize(new ProjectileDestroy
        {
            Key = new ProjectileKey(8, 42, 0)
        });

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.ProjectileDestroy,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            new ProjectileDestroySanityPolicy(),
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
    }

    private static PacketProcessingDecision Process(
        ProjectileDestroy value,
        PacketDirection direction = PacketDirection.Inbound)
    {
        byte[] payload = Serialize(value);
        PacketView packet = PacketView.FromPayload(PacketTypes.ProjectileDestroy, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            direction,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);

        return new ProjectileDestroySanityPolicy().Process(in context);
    }

    private static byte[] Serialize(ProjectileDestroy value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }
}
