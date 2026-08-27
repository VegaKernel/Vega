using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class Phase25RemainingPacketAuditTests
{
    [Test]
    public void ZonesAndNpcSpecialRequireSenderOwnership()
    {
        byte[] zonesPayload = [7, 1, 2, 3, 4, 5, 6];
        byte[] specialPayload = [7, 10];

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(
                    new PlayerPacketOwnershipPolicy(),
                    PacketView.FromPayload(PacketTypes.Zones, zonesPayload),
                    connectionSlot: 8),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
            Assert.That(
                Process(
                    new PlayerPacketOwnershipPolicy(),
                    PacketView.FromPayload(PacketTypes.NpcSpecial, specialPayload),
                    connectionSlot: 8),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
        });
    }

    [Test]
    public void NpcSpecialRejectsUnknownEffectButAllowsProtocol326CakeEffect()
    {
        PlayerActionSanityPolicy policy = PlayerActions();

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(policy, PacketView.FromPayload(PacketTypes.NpcSpecial, [7, 7]), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc_special.effect_out_of_range")));
            Assert.That(
                Process(policy, PacketView.FromPayload(PacketTypes.NpcSpecial, [7, 10]), 7).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    [Test]
    public void SpawnBossRequiresSenderOwnership()
    {
        PacketProcessingDecision decision = Process(
            new PlayerPacketOwnershipPolicy(),
            PacketView.FromPayload(
                PacketTypes.SpawnBossorInvasion,
                Serialize(new SpawnBossorInvasion { PlayerId = 8, Type = -1 })),
            connectionSlot: 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
    }

    [Test]
    public void SpawnBossRejectsUnknownRequestTypes()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Process(NpcActions(), BuildSpawnPacket(7, 1_000), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.spawn_type_out_of_range")));
            Assert.That(
                Process(NpcActions(), BuildSpawnPacket(7, -9), 7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.npc.spawn_type_out_of_range")));
            Assert.That(
                Process(NpcActions(), BuildSpawnPacket(7, -19), 7).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    [Test]
    public void TeleportItemRejectsUnknownProtocol326Subtype()
    {
        PacketProcessingDecision decision = Process(
            new TeleportSanityPolicy(200, static () => 100, static () => 50),
            PacketView.FromPayload(PacketTypes.TeleportationPotion, new byte[] { 5 }),
            connectionSlot: 7);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.teleport_item.subtype_out_of_range")));
    }

    [Test]
    public void PlayerHurtRejectsInvalidTargetAndDamage()
    {
        PacketProcessingDecision invalidTarget = Process(
            PlayerActions(),
            PacketView.FromPayload(
                PacketTypes.PlayerHurtV2,
                Serialize(new PlayerHurtV2 { PlayerId = 8, Damage = 10, HitDirection = 1 })),
            connectionSlot: 7);
        PacketProcessingDecision invalidDamage = Process(
            PlayerActions(),
            PacketView.FromPayload(
                PacketTypes.PlayerHurtV2,
                Serialize(new PlayerHurtV2 { PlayerId = 7, Damage = -1, HitDirection = 1 })),
            connectionSlot: 7);

        Assert.Multiple(() =>
        {
            Assert.That(
                invalidTarget,
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_hurt.target_out_of_range")));
            Assert.That(
                invalidDamage,
                Is.EqualTo(PacketProcessingDecision.Violation("packet.player_hurt.combat_values_out_of_range")));
        });
    }

    [Test]
    public void PlayerDeathRejectsNegativeDamageBeforeNativeKill()
    {
        byte[] payload = new byte[6];
        payload[0] = 7;
        payload[1] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), -1);
        payload[4] = 1;
        payload[5] = 0;

        Assert.That(
            Process(PlayerActions(), PacketView.FromPayload(PacketTypes.PlayerDeathV2, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_death.combat_values_out_of_range")));
    }

    [Test]
    public void CrystalInvasionRejectsOutOfWorldCrystalPosition()
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(payload, 100);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 10);
        var policy = new WorldInteractionSanityPolicy(
            worldWidth: static () => 100,
            worldHeight: static () => 50,
            itemTypeCount: 7_000,
            prefixCount: 100,
            projectileTypeCount: 1_000);

        Assert.That(
            Process(policy, PacketView.FromPayload(PacketTypes.CrystalInvasionStart, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.crystal_invasion.position_out_of_world")));
    }

    [Test]
    public void DisplayDollMiscInventoryRejectsSlotPastNativeArray()
    {
        byte[] payload = new byte[12];
        payload[0] = 7;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), 42);
        payload[5] = 1;
        payload[6] = (byte)DisplayDollSyncCommand.Misc;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(9), 1);
        payload[11] = 0;

        Assert.That(
            Process(WorldMutations(), PacketView.FromPayload(PacketTypes.TileEntityDisplayDollItemSync, payload), 7),
            Is.EqualTo(PacketProcessingDecision.Violation("packet.tile_entity.display_doll_slot_out_of_range")));
    }

    [Test]
    public void PasswordSendRemainsHandshakeOnly()
    {
        Assert.That(
            PacketPipelineClassification.GetIngressDisposition(PacketTypes.PasswordSend),
            Is.EqualTo(PacketIngressDisposition.Handshake));
    }

    [Test]
    public void UnknownNetModuleIdCanReachNativeDispatcher()
    {
        var processor = new PassThroughProcessor();
        byte[] payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, ushort.MaxValue);

        PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            (byte)PacketTypes.LoadNetModule,
            payload,
            new ConnectionId(7),
            new PlayerId(7),
            processor,
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(processor.Called, Is.True);
        });
    }

    [TestCase(PacketTypes.SpawnBossorInvasion, 4)]
    [TestCase(PacketTypes.Teleport, 20)]
    [TestCase(PacketTypes.LoadNetModule, 120)]
    [TestCase(PacketTypes.ToggleParty, 4)]
    [TestCase(PacketTypes.CrystalInvasionStart, 2)]
    [TestCase(PacketTypes.TileEntityDisplayDollItemSync, 60)]
    [TestCase(PacketTypes.FoodPlatterTryPlacing, 30)]
    [TestCase(PacketTypes.TEDeadCellsDisplayJar, 30)]
    public void RemainingHighImpactRequestsHaveScopedRateLimits(PacketTypes packetType, int limit)
    {
        var sessions = new FakeSessions();
        using var policy = new PacketRateLimitPolicy(sessions);
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;

        for (int index = 0; index < limit; index++)
        {
            Assert.That(
                Process(policy, PacketView.FromPayload(packetType, ReadOnlySpan<byte>.Empty), 7, timestamp).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
        }

        Assert.That(
            Process(policy, PacketView.FromPayload(packetType, ReadOnlySpan<byte>.Empty), 7, timestamp),
            Is.EqualTo(new PacketProcessingDecision(
                PacketDecisionKind.Reject,
                $"packet.rate_limit.{packetType}")));
    }

    [Test]
    public void FoodPlatterAndDisplayJarRejectInvalidPlacementData()
    {
        byte[] food = new byte[9];
        BinaryPrimitives.WriteInt16LittleEndian(food, 100);
        BinaryPrimitives.WriteInt16LittleEndian(food.AsSpan(2), 10);
        BinaryPrimitives.WriteInt16LittleEndian(food.AsSpan(4), 1);
        BinaryPrimitives.WriteInt16LittleEndian(food.AsSpan(7), 1);

        byte[] jar = new byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(jar, 10);
        BinaryPrimitives.WriteUInt16LittleEndian(jar.AsSpan(2), 10);
        BinaryPrimitives.WriteInt16LittleEndian(jar.AsSpan(4), 7_000);
        BinaryPrimitives.WriteInt16LittleEndian(jar.AsSpan(7), 1);

        Assert.Multiple(() =>
        {
            Assert.That(
                Process(
                    WorldMutations(),
                    PacketView.FromPayload(PacketTypes.FoodPlatterTryPlacing, food),
                    7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.food_platter.position_out_of_world")));
            Assert.That(
                Process(
                    WorldMutations(),
                    PacketView.FromPayload(PacketTypes.TEDeadCellsDisplayJar, jar),
                    7),
                Is.EqualTo(PacketProcessingDecision.Violation("packet.display_jar.item_type_out_of_range")));
        });
    }

    private static PacketView BuildSpawnPacket(short playerId, short type)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(payload, playerId);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), type);
        return PacketView.FromPayload(PacketTypes.SpawnBossorInvasion, payload);
    }

    private static PlayerActionSanityPolicy PlayerActions() => new(
        playerCount: 8,
        npcCount: 200,
        buffTypeCount: 500,
        projectileTypeCount: 1_000,
        worldWidth: static () => 100,
        worldHeight: static () => 50);

    private static NpcPacketSanityPolicy NpcActions() => new(
        npcCount: 200,
        npcTypeCount: 1_000,
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

    private static byte[] Serialize(TerrariaPacket packet)
    {
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }

    private static PacketProcessingDecision Process(
        IPacketProcessor<PacketView> policy,
        PacketView packet,
        int connectionSlot,
        DateTimeOffset? timestamp = null)
    {
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(connectionSlot),
            new PlayerId(connectionSlot),
            timestamp ?? DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class PassThroughProcessor : IPacketProcessor<PacketView>
    {
        public bool Called { get; private set; }

        public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
        {
            Called = true;
            return PacketProcessingDecision.Allow;
        }
    }

    private sealed class FakeSessions : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened
        {
            add { }
            remove { }
        }

        public event Action<PlayerSessionClosed>? Closed
        {
            add { }
            remove { }
        }
    }
}
