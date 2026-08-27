using System.Buffers.Binary;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TeleportSanityPolicyTests
{
    [Test]
    public void NpcTeleportRejectsOutOfRangeTargetBeforeNativeLookup()
    {
        byte[] payload = new byte[12];
        payload[0] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1), 200);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3), BitConverter.SingleToInt32Bits(16));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7), BitConverter.SingleToInt32Bits(16));

        var context = new PacketProcessingContext<PacketView>(
            PacketView.FromPayload(PacketTypes.Teleport, payload),
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);
        var policy = new TeleportSanityPolicy(200, static () => 100, static () => 50);

        Assert.That(
            policy.Process(in context),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.teleport.npc_target_out_of_range")));
    }

    [Test]
    public void ExplicitTeleportRejectsNonFinitePosition()
    {
        byte[] payload = new byte[12];
        payload[0] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1), 7);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3), BitConverter.SingleToInt32Bits(float.NaN));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7), BitConverter.SingleToInt32Bits(16));

        var context = new PacketProcessingContext<PacketView>(
            PacketView.FromPayload(PacketTypes.Teleport, payload),
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);
        var policy = new TeleportSanityPolicy(200, static () => 100, static () => 50);

        Assert.That(
            policy.Process(in context),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.teleport.non_finite_position")));
    }
}
