using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PacketRateLimitPolicyTests
{
    [Test]
    public void RejectsOnlyAfterTheIncidentBackedPerSecondLimit()
    {
        var sessions = new FakeSessions();
        using var policy = new PacketRateLimitPolicy(sessions);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        PacketProcessingDecision first = Process(policy, PacketTypes.ChestGetContents, 5, timestamp);
        PacketProcessingDecision second = Process(policy, PacketTypes.ChestGetContents, 5, timestamp);
        PacketProcessingDecision third = Process(policy, PacketTypes.ChestGetContents, 5, timestamp);

        Assert.Multiple(() =>
        {
            Assert.That(first.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(second.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(third.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(third.Reason, Is.EqualTo("packet.rate_limit.ChestGetContents"));
        });
    }

    [Test]
    public void NewWindowAndSessionCloseClearConnectionState()
    {
        var sessions = new FakeSessions();
        using var policy = new PacketRateLimitPolicy(sessions);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp);
        Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp);
        Assert.That(Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp).Kind,
            Is.EqualTo(PacketDecisionKind.Reject));

        Assert.That(Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp.AddSeconds(1)).Kind,
            Is.EqualTo(PacketDecisionKind.Allow));
        Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp.AddSeconds(1));
        sessions.Close(7);
        Assert.That(Process(policy, PacketTypes.PlayerDeathV2, 7, timestamp.AddSeconds(1)).Kind,
            Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void ClockRollbackRestartsOnlyTheMatchingWindow()
    {
        var sessions = new FakeSessions();
        using var policy = new PacketRateLimitPolicy(sessions);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        Process(policy, PacketTypes.ChestGetContents, 5, timestamp);
        Process(policy, PacketTypes.ChestGetContents, 5, timestamp);
        Process(policy, PacketTypes.ChestGetContents, 6, timestamp);
        Process(policy, PacketTypes.ChestGetContents, 6, timestamp);

        Assert.That(Process(policy, PacketTypes.ChestGetContents, 5, timestamp.AddTicks(-1)).Kind,
            Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(policy, PacketTypes.ChestGetContents, 5, timestamp).Kind,
            Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(policy, PacketTypes.ChestGetContents, 6, timestamp).Kind,
            Is.EqualTo(PacketDecisionKind.Reject));
    }

    [Test]
    public void PacketWithoutMeasuredLimitIsNotThrottled()
    {
        var sessions = new FakeSessions();
        using var policy = new PacketRateLimitPolicy(sessions);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        for (var index = 0; index < 500; index++)
        {
            Assert.That(Process(policy, PacketTypes.PlayerHp, 1, timestamp).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
        }
    }

    private static PacketProcessingDecision Process(
        PacketRateLimitPolicy policy,
        PacketTypes packetType,
        int slot,
        DateTimeOffset timestamp)
    {
        PacketView packet = PacketView.FromPayload(packetType, ReadOnlySpan<byte>.Empty);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(slot),
            new PlayerId(slot),
            timestamp,
            CancellationToken.None);
        return policy.Process(in context);
    }

    private sealed class FakeSessions : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened
        {
            add { }
            remove { }
        }

        public event Action<PlayerSessionClosed>? Closed;

        public void Close(int slot) => Closed?.Invoke(new PlayerSessionClosed(
            PlayerSessionId.New(),
            new PlayerId(slot),
            "Player",
            DateTimeOffset.UtcNow));
    }
}
