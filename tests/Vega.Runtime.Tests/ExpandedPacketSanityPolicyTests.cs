using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class ExpandedPacketSanityPolicyTests
{
    [Test]
    public void PlayerHealthRejectsNonPositiveValuesBeforeTerrariaMutation()
    {
        var policy = PlayerActions();
        PacketView packet = PacketView.FromPayload(
            PacketTypes.PlayerHp,
            new PlayerHp { PlayerId = 7, Hp = 0, MaxHp = 100 }.ToPayloadArray());

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_health.non_positive")));
    }

    [Test]
    public void PlayerBuffOwnershipReadsProtocol326SenderWithoutMaterialization()
    {
        PacketView packet = PacketView.FromPayload(
            PacketTypes.PlayerBuff,
            new byte[] { 7, 1, 0, 0, 0 });
        var policy = new PlayerPacketOwnershipPolicy();

        PacketProcessingDecision own = Process(policy, packet, 7);
        PacketProcessingDecision foreign = Process(policy, packet, 8);

        Assert.Multiple(() =>
        {
            Assert.That(own.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(foreign, Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
        });
    }

    [Test]
    public void PlayerBuffListRejectsNonzeroValueAfterTerminator()
    {
        var policy = PlayerActions();
        PacketView packet = PacketView.FromPayload(
            PacketTypes.PlayerBuff,
            new byte[]
            {
                7,
                1, 0,
                0, 0,
                2, 0
            });

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_buff_list.nonzero_after_terminator")));
    }

    [Test]
    public void LoadoutIndexOutsideThreeVanillaLoadoutsIsViolation()
    {
        var policy = PlayerActions();
        PacketView packet = PacketView.FromPayload(
            PacketTypes.SyncLoadout,
            new byte[] { 7, 3, 0, 0 });

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Violation("packet.loadout.index_out_of_range")));
    }

    [Test]
    public void PlayerAddBuffValidatesProtocol326UInt16TargetAndBuffRange()
    {
        var policy = PlayerActions();
        byte[] payload = new byte[7];
        payload[0] = 8;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3), 60);
        PacketView packet = PacketView.FromPayload(PacketTypes.PlayerAddBuff, payload);

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_buff_add.target_out_of_range")));
    }

    [Test]
    public void GolfProjectileTypeUsesProtocol326FinalUInt16Field()
    {
        var policy = PlayerActions();
        byte[] payload = new byte[9];
        payload[0] = 7;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(7), 100);
        PacketView packet = PacketView.FromPayload(PacketTypes.LandGolfBallInCup, payload);

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.golf.projectile_type_out_of_range")));
    }

    [Test]
    public void NpcAddBuffReadsUInt16BuffIdInsteadOfMultiplicityByteLayout()
    {
        var policy = NpcActions();
        byte[] payload = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 500);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4), 60);
        PacketView packet = PacketView.FromPayload(PacketTypes.NpcAddBuff, payload);

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.buff_type_out_of_range")));
    }

    [Test]
    public void NpcStrikeSlotIsRejectedBeforeTerrariaIndexesNpcArray()
    {
        var policy = NpcActions();
        byte[] payload = new byte[10];
        payload[0] = 10;
        payload[1] = 1;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 20);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), BitConverter.SingleToInt32Bits(1f));
        payload[8] = 1;
        payload[9] = 0;
        PacketView packet = PacketView.FromPayload(PacketTypes.NpcStrike, payload);

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.strike_id_out_of_range")));
    }

    [Test]
    public void NpcTeleportTargetOutsideRuntimeArrayIsViolation()
    {
        var policy = new TeleportSanityPolicy(
            npcCount: 10,
            worldWidth: static () => 100,
            worldHeight: static () => 100);
        PacketView packet = PacketView.FromPayload(
            PacketTypes.Teleport,
            BuildTeleportPayload(flags: 1, target: 10, x: 16f, y: 16f));

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.teleport.npc_target_out_of_range")));
    }

    [Test]
    public void OversizedClientTileRectangleIsRejectedBeforePayloadDecompression()
    {
        var policy = new WorldInteractionSanityPolicy(
            worldWidth: static () => 500,
            worldHeight: static () => 500,
            itemTypeCount: 1000,
            prefixCount: 100,
            projectileTypeCount: 1000);
        byte[] payload = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(), 1);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 1);
        payload[4] = 65;
        payload[5] = 64;
        payload[6] = 0;
        PacketView packet = PacketView.FromPayload(PacketTypes.TileSendSquare, payload);

        PacketProcessingDecision decision = Process(policy, packet, 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.tile_square.rectangle_too_large")));
    }

    private static PlayerActionSanityPolicy PlayerActions() => new(
        playerCount: 8,
        npcCount: 10,
        buffTypeCount: 500,
        projectileTypeCount: 100,
        worldWidth: static () => 100,
        worldHeight: static () => 100);

    private static NpcPacketSanityPolicy NpcActions() => new(
        npcCount: 10,
        npcTypeCount: 1000,
        buffTypeCount: 500,
        playerCount: 8,
        worldWidth: static () => 100,
        worldHeight: static () => 100);

    private static PacketProcessingDecision Process(
        IPacketProcessor<PacketView> policy,
        PacketView packet,
        int connectionSlot)
    {
        PacketProcessingContext<PacketView> context = Context(packet, connectionSlot);
        return policy.Process(in context);
    }

    private static PacketProcessingContext<PacketView> Context(PacketView packet, int connectionSlot) =>
        new(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(connectionSlot),
            null,
            DateTimeOffset.UtcNow);

    private static byte[] BuildTeleportPayload(byte flags, short target, float x, float y, byte style = 0)
    {
        byte[] payload = new byte[12];
        payload[0] = flags;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(1), target);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(3), BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(7), BitConverter.SingleToInt32Bits(y));
        payload[11] = style;
        return payload;
    }
}
