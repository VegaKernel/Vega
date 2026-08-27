using System.Buffers.Binary;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class Phase25PacketAuditTests
{
    [Test]
    public void NpcTalkRequiresSenderOwnershipAndValidNpcTarget()
    {
        byte[] payload = new byte[3];
        payload[0] = 7;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1), 10);
        PacketView packet = PacketView.FromPayload(PacketTypes.NpcTalk, payload);

        PacketProcessingDecision ownership = Process(new PlayerPacketOwnershipPolicy(), packet, 8);
        PacketProcessingDecision target = Process(NpcActions(), packet, 7);

        Assert.Multiple(() =>
        {
            Assert.That(
                ownership,
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
            Assert.That(
                target,
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.talk_id_out_of_range")));
        });
    }

    [Test]
    public void NpcAddBuffRejectsMalformedPayloadAndNegativeDuration()
    {
        PacketView malformed = PacketView.FromPayload(PacketTypes.NpcAddBuff, new byte[5]);

        byte[] negativeDurationPayload = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(negativeDurationPayload.AsSpan(), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(negativeDurationPayload.AsSpan(2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(negativeDurationPayload.AsSpan(4), -1);
        PacketView negativeDuration = PacketView.FromPayload(PacketTypes.NpcAddBuff, negativeDurationPayload);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(NpcActions(), malformed, 7),
                Is.EqualTo(PacketProcessingDecision.Reject("packet.npc.buff_malformed")));
            Assert.That(
                Process(NpcActions(), negativeDuration, 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.buff_time_out_of_range")));
        });
    }

    [Test]
    public void PlayerAddBuffRejectsOutOfRangeBuffAndNegativeDuration()
    {
        byte[] invalidBuffPayload = new byte[7];
        invalidBuffPayload[0] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(invalidBuffPayload.AsSpan(1), 500);
        BinaryPrimitives.WriteInt32LittleEndian(invalidBuffPayload.AsSpan(3), 60);

        byte[] negativeDurationPayload = new byte[7];
        negativeDurationPayload[0] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(negativeDurationPayload.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(negativeDurationPayload.AsSpan(3), -1);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(PlayerActions(), PacketView.FromPayload(PacketTypes.PlayerAddBuff, invalidBuffPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_buff_add.type_out_of_range")));
            Assert.That(
                Process(PlayerActions(), PacketView.FromPayload(PacketTypes.PlayerAddBuff, negativeDurationPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_buff_add.time_out_of_range")));
        });
    }

    [Test]
    public void UpdateNpcHomeRejectsInvalidNpcAndOutOfWorldHome()
    {
        byte[] invalidNpcPayload = BuildNpcHomePayload(npcId: 10, x: 10, y: 10);
        byte[] invalidPositionPayload = BuildNpcHomePayload(npcId: 3, x: 100, y: 10);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.UpdateNPCHome, invalidNpcPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.home_id_out_of_range")));
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.UpdateNPCHome, invalidPositionPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.home_position_out_of_world")));
        });
    }

    [Test]
    public void SignReadRejectsOutOfWorldCoordinates()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), 100);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 10);
        PacketView packet = PacketView.FromPayload(PacketTypes.SignRead, payload);

        Assert.That(
            Process(WorldMutations(), packet, 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.sign.position_out_of_world")));
    }

    private static byte[] BuildNpcHomePayload(short npcId, short x, short y)
    {
        byte[] payload = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), npcId);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), x);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), y);
        payload[6] = 0;
        return payload;
    }

    private static PlayerActionSanityPolicy PlayerActions() => new(
        playerCount: 8,
        npcCount: 10,
        buffTypeCount: 500,
        projectileTypeCount: 100,
        worldWidth: static () => 100,
        worldHeight: static () => 50);

    private static NpcPacketSanityPolicy NpcActions() => new(
        npcCount: 10,
        npcTypeCount: 1000,
        buffTypeCount: 500,
        playerCount: 8,
        worldWidth: static () => 100,
        worldHeight: static () => 50);

    private static WorldMutationSanityPolicy WorldMutations() => new(
        worldWidth: static () => 100,
        worldHeight: static () => 50,
        chestCount: static () => 10,
        inventorySlotCount: static () => 10,
        tileTypeCount: 500,
        wallTypeCount: 200,
        itemTypeCount: 7_000,
        prefixCount: 100);

    private static PacketProcessingDecision Process(
        IPacketProcessor<PacketView> policy,
        PacketView packet,
        int connectionSlot)
    {
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(connectionSlot),
            null,
            DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }
}