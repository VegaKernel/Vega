using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerPacketOwnershipPolicyTests
{
    private static readonly PacketTypes[] OwnedPacketTypes =
    [
        PacketTypes.PlayerUpdate,
        PacketTypes.PlayerHp,
        PacketTypes.PlayerMana,
        PacketTypes.PlayerTeam,
        PacketTypes.TeamChangeFromUI,
        PacketTypes.TogglePvp,
        PacketTypes.SpectatePlayer,
        PacketTypes.PlayerTeleportPortal,
        PacketTypes.PlayerDeathV2,
        PacketTypes.SyncPlayerChestIndex,
        PacketTypes.TileEntityDisplayDollItemSync,
        PacketTypes.RequestTileEntityInteraction,
        PacketTypes.TileEntityHatRackItemSync,
        PacketTypes.SyncTilePicking,
        PacketTypes.SyncProjectileTrackers,
        PacketTypes.Emoji,
        PacketTypes.SyncLoadout
    ];

    [TestCaseSource(nameof(OwnedPacketTypes))]
    public void MatchingConnectionSlotIsAllowed(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 7);
        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [TestCaseSource(nameof(OwnedPacketTypes))]
    public void ForeignPlayerSlotIsViolation(PacketTypes packetType)
    {
        PacketProcessingDecision decision = Process(packetType, connectionSlot: 7, claimedSlot: 8);
        Assert.That(decision, Is.EqualTo(PacketProcessingDecision.Violation("packet.player_identity_mismatch")));
    }

    [Test]
    public void OutboundPlayerStateIsNotTreatedAsClientForgery()
    {
        PacketView packet = PacketView.FromPayload(
            PacketTypes.PlayerDeathV2,
            Serialize(CreateOwnedPacket(PacketTypes.PlayerDeathV2, 8)));
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Outbound,
            new ConnectionId(7),
            null,
            DateTimeOffset.UtcNow);

        PacketProcessingDecision decision = new PlayerPacketOwnershipPolicy().Process(in context);
        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void UnrelatedPacketIsIgnored()
    {
        var packet = PacketView.FromPayload(PacketTypes.PlayerActive, new byte[] { 8, 1 });
        var context = Context(packet, connectionSlot: 7);

        PacketProcessingDecision decision = new PlayerPacketOwnershipPolicy().Process(in context);
        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    private static PacketProcessingDecision Process(PacketTypes packetType, int connectionSlot, byte claimedSlot)
    {
        PacketView packet = PacketView.FromPayload(packetType, Serialize(CreateOwnedPacket(packetType, claimedSlot)));
        var context = Context(packet, connectionSlot);
        return new PlayerPacketOwnershipPolicy().Process(in context);
    }

    private static TerrariaPacket CreateOwnedPacket(PacketTypes packetType, byte playerId) => packetType switch
    {
        PacketTypes.PlayerUpdate => new PlayerUpdate { PlayerId = playerId },
        PacketTypes.PlayerHp => new PlayerHp { PlayerId = playerId, Hp = 100, MaxHp = 100 },
        PacketTypes.PlayerMana => new PlayerMana { PlayerId = playerId, Mana = 20, MaxMana = 20 },
        PacketTypes.PlayerTeam => new PlayerTeam { PlayerId = playerId, Team = 1 },
        PacketTypes.TeamChangeFromUI => new TeamChangeFromUI { PlayerId = playerId, Team = 1 },
        PacketTypes.TogglePvp => new TogglePvp { PlayerId = playerId, PvpEnabled = true },
        PacketTypes.SpectatePlayer => new SpectatePlayer { PlayerId = playerId, TargetPlayerId = 2 },
        PacketTypes.PlayerTeleportPortal => new PlayerTeleportPortal { PlayerId = playerId },
        PacketTypes.PlayerDeathV2 => new PlayerDeathV2 { PlayerId = playerId, Damage = 100 },
        PacketTypes.SyncPlayerChestIndex => new SyncPlayerChestIndex { Player = playerId, Chest = -1 },
        PacketTypes.TileEntityDisplayDollItemSync => new TileEntityDisplayDollItemSync
        {
            PlayerId = playerId,
            ItemData = new ItemSyncData()
        },
        PacketTypes.RequestTileEntityInteraction => new RequestTileEntityInteraction { PlayerId = playerId },
        PacketTypes.TileEntityHatRackItemSync => new TileEntityHatRackItemSync
        {
            PlayerId = playerId,
            ItemData = new ItemSyncData()
        },
        PacketTypes.SyncTilePicking => new SyncTilePicking { PlayerId = playerId, TileX = 1, TileY = 1 },
        PacketTypes.SyncProjectileTrackers => new SyncProjectileTrackers
        {
            PlayerId = playerId,
            PiggyBankProjectile = new TrackedProjectileReferenceData(),
            VoidLensChestProjectile = new TrackedProjectileReferenceData()
        },
        PacketTypes.Emoji => new Emoji { PlayerId = playerId, EmojiId = 1 },
        PacketTypes.SyncLoadout => new SyncLoadout { PlayerId = playerId, LoadoutIndex = 0 },
        _ => throw new ArgumentOutOfRangeException(nameof(packetType))
    };

    private static byte[] Serialize(TerrariaPacket packet)
    {
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        return stream.ToArray();
    }

    private static PacketProcessingContext<PacketView> Context(PacketView packet, int connectionSlot) =>
        new(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(connectionSlot),
            null,
            DateTimeOffset.UtcNow);
}
