using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class SecuritySignalStoreTests
{
    [Test]
    public void BoundedStoreRedactsEndpointsUnlessSensitiveAccessIsExplicit()
    {
        var store = new BoundedSecuritySignalStore(2);
        store.Record(Signal(1));
        store.Record(Signal(2));
        store.Record(Signal(3));

        IReadOnlyList<SecuritySignal> redacted = store.Read(SecuritySignalExposure.Redacted);
        IReadOnlyList<SecuritySignal> sensitive = store.Read(SecuritySignalExposure.Sensitive);

        Assert.Multiple(() =>
        {
            Assert.That(redacted.Select(signal => signal.PacketId), Is.EqualTo(new byte[] { 3, 2 }));
            Assert.That(redacted, Has.All.Property(nameof(SecuritySignal.EffectiveEndpoint)).Null);
            Assert.That(redacted, Has.All.Property(nameof(SecuritySignal.TransportEndpoint)).Null);
            Assert.That(sensitive[0].EffectiveEndpoint, Is.EqualTo("203.0.113.3:7777"));
            Assert.That(sensitive[0].ForwardingProvenance,
                Is.EqualTo(ConnectionForwardingProvenance.Direct));
        });
    }

    private static SecuritySignal Signal(byte packetId) =>
        new(
            DateTimeOffset.UtcNow,
            new ConnectionId(4),
            packetId,
            PacketDecisionKind.Violation,
            "packet.test",
            $"203.0.113.{packetId}:7777",
            $"203.0.113.{packetId}:7777",
            ConnectionForwardingProvenance.Direct);
}
