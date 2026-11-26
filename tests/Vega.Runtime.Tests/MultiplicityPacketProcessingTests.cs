using Multiplicity.Packets;
using Multiplicity.Packets.Models;
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
        var payloadWindow = new byte[]
        {
            7,
            100, 0,
            120, 0,
            0x7f
        };

        var decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.PlayerHp,
            payloadWindow,
            0,
            payloadWindow.Length,
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
    public void Invalid_payload_is_rejected_before_policy_processor_runs()
    {
        var processor = new CapturingProcessor();
        var payload = new byte[4];

        var decision = MultiplicityPacketProcessing.ProcessInboundPayload(
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

    [Test]
    public void Variable_length_payload_uses_only_the_occupied_receive_window()
    {
        using var stream = new MemoryStream();
        new PlayerUpdate { PlayerId = 7 }.ToStream(stream, includeHeader: false);
        byte[] payload = stream.ToArray();
        var receiveBuffer = new byte[128 * 1024];
        const int offset = 128;
        payload.CopyTo(receiveBuffer, offset);
        var processor = new CapturingProcessor();
        var decision = MultiplicityPacketProcessing.ProcessInboundPayload((byte)PacketTypes.PlayerUpdate,
            receiveBuffer, offset, payload.Length, new ConnectionId(7), new PlayerId(7), processor, DateTimeOffset.UtcNow);
        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(processor.PacketType, Is.EqualTo(PacketTypes.PlayerUpdate));
        Assert.That(processor.PayloadLength, Is.EqualTo(payload.Length));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Player_update_adapter_matches_general_parser_for_all_optional_fields(bool trailingBytes)
    {
        for (int fields = 0; fields < 16; fields++)
        {
            var update = new PlayerUpdate
            {
                PlayerId = 7,
                PositionX = 320,
                PositionY = 480,
                VelocityX = 1.25f,
                VelocityY = -0.5f,
                MountType = 1,
                PotionOfReturnOriginalPositionX = 300,
                PotionOfReturnOriginalPositionY = 450,
                PotionOfReturnHomePositionX = 100,
                PotionOfReturnHomePositionY = 150,
                CameraTargetX = 320,
                CameraTargetY = 480,
                MovementFlags = ((fields & 1) != 0 ? UpdatePlayerMovementFlags.HasVelocity : 0) |
                    ((fields & 2) != 0 ? UpdatePlayerMovementFlags.HasMount : 0),
                MiscFlags1 = (fields & 4) != 0 ? UpdatePlayerMiscFlags1.HasPotionOfReturnPositions : 0,
                MiscFlags2 = (fields & 8) != 0 ? UpdatePlayerMiscFlags2.HasCameraTarget : 0
            };
            using var stream = new MemoryStream();
            update.ToStream(stream, includeHeader: false);
            byte[] payload = stream.ToArray();
            const int offset = 11;
            var buffer = new byte[offset + payload.Length + 4];
            payload.CopyTo(buffer, offset);
            int length = payload.Length + (trailingBytes ? 4 : 0);
            Assert.That(PacketViewParser.TryParsePayload(PacketTypes.PlayerUpdate, buffer.AsMemory(offset, length),
                out var reference, out int consumed), Is.True);
            Assert.That(MultiplicityPacketProcessing.TryParseInboundPayload((byte)PacketTypes.PlayerUpdate,
                buffer, offset, length, out var actual), Is.True);
            Assert.That(actual.PayloadSpan.SequenceEqual(reference.PayloadSpan), Is.True);
            Assert.That(actual.PayloadSpan.Length, Is.EqualTo(consumed));
            Assert.That(actual.AsPlayerUpdateView().PlayerId, Is.EqualTo(update.PlayerId));
        }
    }

    [Test, NonParallelizable]
    public void Player_update_honors_custom_deserializer()
    {
        var original = TerrariaPacket.DeserializerMap[PacketTypes.PlayerUpdate];
        bool called = false;
        try
        {
            TerrariaPacket.DeserializerMap[PacketTypes.PlayerUpdate] = reader =>
            {
                called = true;
                return original(reader);
            };
            using var stream = new MemoryStream();
            new PlayerUpdate { PlayerId = 7 }.ToStream(stream, includeHeader: false);
            byte[] payload = stream.ToArray();
            Assert.That(MultiplicityPacketProcessing.TryParseInboundPayload((byte)PacketTypes.PlayerUpdate,
                payload, 0, payload.Length, out _), Is.True);
            Assert.That(called, Is.True);
        }
        finally
        {
            TerrariaPacket.DeserializerMap[PacketTypes.PlayerUpdate] = original;
        }
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
            return new PacketProcessingDecision(PacketDecisionKind.Allow);
        }
    }
}
