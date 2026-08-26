using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaInboundPacketGuardTests
{
    [Test]
    public void ServerOnlyPacketIsAlwaysRejected()
    {
        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.NpcItemStrike,
            TerrariaConnectionState.FullyConnected,
            3,
            ReadOnlySpan<byte>.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo("packet.ingress.server_only"));
        });
    }

    [Test]
    public void GameplayPacketBeforeLoginIsRejected()
    {
        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.PlayerHp,
            TerrariaConnectionState.SendingWorld,
            3,
            new byte[5]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo("packet.ingress.gameplay_before_login"));
        });
    }

    [Test]
    public void ReservedPlayerNamePrefixIsRejected()
    {
        var packet = new PlayerInfo
        {
            PlayerId = 4,
            Name = "tsi:probe"
        };

        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.PlayerInfo,
            TerrariaConnectionState.ProtocolAccepted,
            4,
            packet.ToPayloadArray());

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_info.reserved_name_prefix"));
        });
    }

    [Test]
    public void PlayerSlotRejectsInvalidFlagsBeforeTerrariaMutation()
    {
        var packet = new PlayerSlot
        {
            PlayerId = 4,
            SlotId = 0,
            Stack = 1,
            Prefix = 0,
            ItemNetId = 0,
            ItemFlags = 0x80
        };

        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.PlayerSlot,
            TerrariaConnectionState.ProtocolAccepted,
            4,
            packet.ToPayloadArray());

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Violation));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_slot.flags_out_of_range"));
        });
    }

    [Test]
    public void PlayerSpawnRequiresProtocol326FifteenBytePayload()
    {
        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.PlayerSpawn,
            TerrariaConnectionState.SendingWorld,
            4,
            new byte[5]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.player_spawn.malformed"));
        });
    }

    [Test]
    public void PlayerSpawnRejectsUnknownContextBeforeTerrariaMutation()
    {
        byte[] payload = new byte[15];
        payload[14] = 4;

        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.PlayerSpawn,
            TerrariaConnectionState.SendingWorld,
            4,
            payload);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.player_spawn.context_out_of_range")));
    }

    [Test]
    public void TileGetSectionRequiresProtocol326TeamByte()
    {
        PacketProcessingDecision decision = TerrariaInboundPacketGuard.Validate(
            (byte)PacketTypes.TileGetSection,
            TerrariaConnectionState.SendingWorld,
            4,
            new byte[8]);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Reject));
            Assert.That(decision.Reason, Is.EqualTo("packet.tile_get_section.malformed"));
        });
    }
}
