using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerStateSanityPolicyTests
{
    [Test]
    public void ValidPlayerUpdateIsAllowed()
    {
        PacketProcessingDecision decision = Process(new PlayerUpdate
        {
            PlayerId = 7,
            SelectedItem = 5,
            PositionX = 160,
            PositionY = 320,
            MovementFlags = UpdatePlayerMovementFlags.HasVelocity,
            VelocityX = 2,
            VelocityY = -2
        });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void NonFinitePositionIsViolation(float value)
    {
        PacketProcessingDecision decision = Process(new PlayerUpdate
        {
            PlayerId = 7,
            PositionX = value,
            PositionY = 10
        });

        Assert.That(decision.Reason, Is.EqualTo("packet.player.non_finite_position"));
    }

    [TestCase(50_001f)]
    [TestCase(float.PositiveInfinity)]
    public void InvalidVelocityIsViolation(float value)
    {
        PacketProcessingDecision decision = Process(new PlayerUpdate
        {
            PlayerId = 7,
            PositionX = 160,
            PositionY = 320,
            MovementFlags = UpdatePlayerMovementFlags.HasVelocity,
            VelocityX = value
        });

        Assert.That(decision.Reason, Is.EqualTo("packet.player.invalid_velocity"));
    }

    [Test]
    public void PositionOutsideWorldIsViolation()
    {
        PacketProcessingDecision decision = Process(new PlayerUpdate
        {
            PlayerId = 7,
            PositionX = 100 * 16,
            PositionY = 10
        });

        Assert.That(decision.Reason, Is.EqualTo("packet.player.position_out_of_world"));
    }

    [Test]
    public void SelectedItemOutsideInventoryIsViolation()
    {
        PacketProcessingDecision decision = Process(new PlayerUpdate
        {
            PlayerId = 7,
            SelectedItem = 10,
            PositionX = 160,
            PositionY = 320
        });

        Assert.That(decision.Reason, Is.EqualTo("packet.player.selected_item_out_of_range"));
    }

    [Test]
    public void PortalTeleportValidatesColorAndWorldPosition()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProcessPortal(new PlayerTeleportPortal
            {
                PlayerId = 7,
                PortalColorIndex = 512,
                NewPositionX = 10,
                NewPositionY = 10
            }).Reason, Is.EqualTo("packet.portal.color_out_of_range"));
            Assert.That(ProcessPortal(new PlayerTeleportPortal
            {
                PlayerId = 7,
                PortalColorIndex = 0,
                NewPositionX = float.NaN,
                NewPositionY = 10
            }).Reason, Is.EqualTo("packet.portal.non_finite_position"));
            Assert.That(ProcessPortal(new PlayerTeleportPortal
            {
                PlayerId = 7,
                PortalColorIndex = 0,
                NewPositionX = 100 * 16 + 1,
                NewPositionY = 10
            }).Reason, Is.EqualTo("packet.portal.position_out_of_world"));
        });
    }

    private static PacketProcessingDecision ProcessPortal(PlayerTeleportPortal value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(PacketTypes.PlayerTeleportPortal, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        return new PlayerStateSanityPolicy(() => 100, () => 50, () => 10).Process(in context);
    }

    private static PacketProcessingDecision Process(PlayerUpdate value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(PacketTypes.PlayerUpdate, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);
        var policy = new PlayerStateSanityPolicy(() => 100, () => 50, () => 10);

        return policy.Process(in context);
    }
}
