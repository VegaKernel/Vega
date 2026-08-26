using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class MultiplicityPacketProcessingTests
{
    [Test]
    public void Inbound_payload_uses_typed_view_and_preserves_context_metadata()
    {
        var processor = new CapturingProcessor();
        var timestamp = new DateTimeOffset(2026, 8, 20, 6, 0, 0, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        byte[] payload = [7, 100, 0, 120, 0];

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerHp,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            processor,
            timestamp,
            cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(processor.Called, Is.True);
            Assert.That(processor.PacketType, Is.EqualTo(PacketTypes.PlayerHp));
            Assert.That(processor.PayloadLength, Is.EqualTo(5));
            Assert.That(processor.Direction, Is.EqualTo(PacketDirection.Inbound));
            Assert.That(processor.ConnectionId, Is.EqualTo(new ConnectionId(7)));
            Assert.That(processor.PlayerId, Is.EqualTo(new PlayerId(7)));
            Assert.That(processor.Timestamp, Is.EqualTo(timestamp));
            Assert.That(processor.CancellationToken, Is.EqualTo(cancellation.Token));
        });
    }

    [Test]
    public void Trailing_bytes_inside_declared_payload_are_rejected()
    {
        var processor = new CapturingProcessor();
        byte[] payload = [7, 100, 0, 120, 0, 0x7f];

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerHp,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            processor,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.invalid_payload"));
            Assert.That(processor.Called, Is.False);
        });
    }

    [Test]
    public void Invalid_payload_is_rejected_before_policy_processor_runs()
    {
        var processor = new CapturingProcessor();
        var payload = new byte[4];

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerHp,
            payload,
            0,
            payload.Length,
            new ConnectionId(3),
            null,
            processor,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.invalid_payload"));
            Assert.That(processor.Called, Is.False);
        });
    }

    [TestCase(PacketTypes.PlayerSpawn, 15)]
    [TestCase(PacketTypes.TileGetSection, 9)]
    [TestCase(PacketTypes.NpcAddBuff, 6)]
    [TestCase(PacketTypes.PlayerAddBuff, 7)]
    public void Protocol326AuditedCompatibilityLayoutsUseExactLengths(PacketTypes packetType, int expectedLength)
    {
        var accepted = new CapturingProcessor();
        byte[] exact = new byte[expectedLength];

        PacketProcessingDecision exactDecision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)packetType,
            exact,
            0,
            exact.Length,
            new ConnectionId(2),
            new PlayerId(2),
            accepted,
            DateTimeOffset.UtcNow);

        var rejected = new CapturingProcessor();
        byte[] trailing = new byte[expectedLength + 1];
        PacketProcessingDecision trailingDecision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)packetType,
            trailing,
            0,
            trailing.Length,
            new ConnectionId(2),
            new PlayerId(2),
            rejected,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(exactDecision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(accepted.Called, Is.True);
            Assert.That(accepted.PayloadLength, Is.EqualTo(expectedLength));
            Assert.That(trailingDecision, Is.EqualTo(PacketProcessingDecision.Reject("packet.invalid_payload")));
            Assert.That(rejected.Called, Is.False);
        });
    }

    [Test]
    public void Protocol326PlayerBuffAcceptsBoundedUInt16ListWithoutMaterialization()
    {
        var processor = new CapturingProcessor();
        byte[] payload =
        [
            7,
            1, 0,
            0x2c, 0x01,
            0, 0
        ];

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerBuff,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            processor,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(processor.Called, Is.True);
            Assert.That(processor.PayloadLength, Is.EqualTo(payload.Length));
        });
    }

    [Test]
    public void Protocol326PlayerBuffRejectsOddUInt16TailBeforePoliciesRun()
    {
        var processor = new CapturingProcessor();
        byte[] payload = [7, 1, 0, 0x7f];

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerBuff,
            payload,
            0,
            payload.Length,
            new ConnectionId(7),
            new PlayerId(7),
            processor,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Reject("packet.invalid_payload")));
            Assert.That(processor.Called, Is.False);
        });
    }

    private sealed class CapturingProcessor : IPacketProcessor<PacketView>
    {
        public bool Called { get; private set; }

        public PacketTypes PacketType { get; private set; }

        public int PayloadLength { get; private set; }

        public PacketDirection Direction { get; private set; }

        public ConnectionId ConnectionId { get; private set; }

        public PlayerId? PlayerId { get; private set; }

        public DateTimeOffset Timestamp { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
        {
            Called = true;
            PacketType = context.Packet.PacketType;
            PayloadLength = context.Packet.PayloadSpan.Length;
            Direction = context.Direction;
            ConnectionId = context.ConnectionId;
            PlayerId = context.PlayerId;
            Timestamp = context.Timestamp;
            CancellationToken = context.CancellationToken;
            return PacketProcessingDecision.Allow;
        }
    }
}
