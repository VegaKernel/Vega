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

    [Test]
    public void PaintPacketsRejectInvalidProtocolValues()
    {
        byte[] tilePayload = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(tilePayload.AsSpan(), 10);
        BinaryPrimitives.WriteInt16LittleEndian(tilePayload.AsSpan(2), 10);
        tilePayload[4] = 32;

        byte[] wallPayload = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(wallPayload.AsSpan(), 10);
        BinaryPrimitives.WriteInt16LittleEndian(wallPayload.AsSpan(2), 10);
        wallPayload[4] = 1;
        wallPayload[5] = 2;

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(WorldMutations(), PacketView.FromPayload(PacketTypes.PaintTile, tilePayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.paint.value_out_of_range")));
            Assert.That(
                Process(WorldMutations(), PacketView.FromPayload(PacketTypes.PaintWall, wallPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.paint.value_out_of_range")));
        });
    }

    [Test]
    public void PlaceObjectRejectsOutOfRangeTileType()
    {
        byte[] payload = new byte[11];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), 10);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 10);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), 500);

        Assert.That(
            Process(WorldMutations(), PacketView.FromPayload(PacketTypes.PlaceObject, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.object.type_out_of_range")));
    }

    [Test]
    public void TileEntityAndGemLockRejectOutOfWorldCoordinates()
    {
        byte[] tileEntityPayload = new byte[5];
        BinaryPrimitives.WriteInt16LittleEndian(tileEntityPayload.AsSpan(), -1);
        BinaryPrimitives.WriteInt16LittleEndian(tileEntityPayload.AsSpan(2), 10);

        byte[] gemLockPayload = new byte[5];
        BinaryPrimitives.WriteInt16LittleEndian(gemLockPayload.AsSpan(), 100);
        BinaryPrimitives.WriteInt16LittleEndian(gemLockPayload.AsSpan(2), 10);
        gemLockPayload[4] = 1;

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(WorldMutations(), PacketView.FromPayload(PacketTypes.PlaceTileEntity, tileEntityPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.tile_entity.position_out_of_world")));
            Assert.That(
                Process(WorldMutations(), PacketView.FromPayload(PacketTypes.GemLockToggle, gemLockPayload), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.gem_lock.position_out_of_world")));
        });
    }

    [Test]
    public void MassWireRejectsOutOfWorldEndpoint()
    {
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), 1);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), 100);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6), 2);

        Assert.That(
            Process(WorldMutations(), PacketView.FromPayload(PacketTypes.MassWireOperation, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.mass_wire.position_out_of_world")));
    }

    [Test]
    public void PlayerHealOtherRejectsInvalidTargetAndNegativeAmount()
    {
        byte[] invalidTarget = new byte[3];
        invalidTarget[0] = 8;
        BinaryPrimitives.WriteInt16LittleEndian(invalidTarget.AsSpan(1), 10);

        byte[] negativeAmount = new byte[3];
        negativeAmount[0] = 7;
        BinaryPrimitives.WriteInt16LittleEndian(negativeAmount.AsSpan(1), -1);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(PlayerActions(), PacketView.FromPayload(PacketTypes.PlayerHealOther, invalidTarget), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_heal.target_out_of_range")));
            Assert.That(
                Process(PlayerActions(), PacketView.FromPayload(PacketTypes.PlayerHealOther, negativeAmount), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_heal.amount_out_of_range")));
        });
    }

    [Test]
    public void CatchNpcRejectsOutOfRangeSlotBeforeLiveArrayLookup()
    {
        byte[] payload = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(payload, 10);

        Assert.That(
            Process(NpcActions(), PacketView.FromPayload(PacketTypes.CatchNPC, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.catch_id_out_of_range")));
    }

    [Test]
    public void ReleaseNpcRejectsInvalidTypeAndWorldPosition()
    {
        byte[] invalidType = BuildReleaseNpcPayload(x: 10, y: 10, type: 1000);
        byte[] invalidPosition = BuildReleaseNpcPayload(x: 1600, y: 10, type: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.ReleaseNPC, invalidType), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.release_type_out_of_range")));
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.ReleaseNPC, invalidPosition), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.release_position_out_of_world")));
        });
    }

    [Test]
    public void FishOutNpcRejectsInvalidTypeAndWorldPosition()
    {
        byte[] invalidType = BuildFishOutNpcPayload(x: 10, y: 10, type: 1000);
        byte[] invalidPosition = BuildFishOutNpcPayload(x: 100, y: 10, type: 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.FishOutNPC, invalidType), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.fish_type_out_of_range")));
            Assert.That(
                Process(NpcActions(), PacketView.FromPayload(PacketTypes.FishOutNPC, invalidPosition), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.fish_position_out_of_world")));
        });
    }

    [Test]
    public void GolfCupRequiresSenderOwnershipAndValidWorldPosition()
    {
        byte[] payload = new byte[9];
        payload[0] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(7), 1);
        PacketView packet = PacketView.FromPayload(PacketTypes.LandGolfBallInCup, payload);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(new PlayerPacketOwnershipPolicy(), packet, 8),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
            Assert.That(
                Process(PlayerActions(), packet, 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.golf.cup_out_of_world")));
        });
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

    private static byte[] BuildReleaseNpcPayload(int x, int y, short type)
    {
        byte[] payload = new byte[11];
        BinaryPrimitives.WriteInt32LittleEndian(payload, x);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), y);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(8), type);
        payload[10] = 0;
        return payload;
    }

    private static byte[] BuildFishOutNpcPayload(ushort x, ushort y, short type)
    {
        byte[] payload = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, x);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), y);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), type);
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
