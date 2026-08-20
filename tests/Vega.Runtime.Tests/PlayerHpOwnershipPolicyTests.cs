using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using TShockNG.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerHpOwnershipPolicyTests
{
    [Test]
    public void Matching_connection_slot_is_allowed()
    {
        var policy = new PlayerHpOwnershipPolicy();
        var packet = PacketView.FromPayload(
            PacketTypes.PlayerHp,
            new byte[] { 7, 100, 0, 120, 0 });
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);

        var decision = policy.Process(in context);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Continue));
    }

    [Test]
    public void Foreign_player_slot_is_rejected()
    {
        var policy = new PlayerHpOwnershipPolicy();
        var packet = PacketView.FromPayload(
            PacketTypes.PlayerHp,
            new byte[] { 8, 100, 0, 120, 0 });
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);

        var decision = policy.Process(in context);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_identity_mismatch"));
        });
    }
}
