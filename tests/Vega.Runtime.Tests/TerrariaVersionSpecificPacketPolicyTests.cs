using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaVersionSpecificPacketPolicyTests
{
    [Test]
    public void TruncatedTileRectangleIsRejectedBeforeNativeDecompression()
    {
        byte[] payload = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(2), 1);
        payload[4] = 1;
        payload[5] = 1;
        PacketView packet = PacketView.FromPayload(PacketTypes.TileSendSquare, payload);
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);

        PacketProcessingDecision decision = new TerrariaTileRectanglePayloadPolicy().Process(in context);

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Reject("packet.tile_square.malformed_payload")));
    }

    [Test]
    public void CattivaDigStateIsRejected()
    {
        PacketProcessingDecision decision = Process(
            new TerrariaProjectileExploitPolicy(),
            new ProjectileNew
            {
                Key = new ProjectileKey(7, 42, 0),
                Type = global::Terraria.ID.ProjectileID.PalworldMinionCattiva,
                AI0 = 3f
            });

        Assert.That(
            decision,
            Is.EqualTo(PacketProcessingDecision.Violation("packet.projectile.cattiva_dig_build_bypass")));
    }

    [Test]
    public void StaleNpcStrikeGenerationIsRejected()
    {
        const int slot = 3;
        global::Terraria.NPC? previous = global::Terraria.Main.npc[slot];
        try
        {
            global::Terraria.Main.npc[slot] = new global::Terraria.NPC { generation = 4 };
            PacketProcessingDecision decision = Process(
                new TerrariaNpcGenerationPolicy(),
                new NpcStrike { NpcSlot = slot, Generation = 3 });

            Assert.That(
                decision,
                Is.EqualTo(PacketProcessingDecision.Reject("packet.npc.stale_generation")));
        }
        finally
        {
            global::Terraria.Main.npc[slot] = previous!;
        }
    }

    private static PacketProcessingDecision Process(
        IPacketProcessor<PacketView> policy,
        TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }
}
