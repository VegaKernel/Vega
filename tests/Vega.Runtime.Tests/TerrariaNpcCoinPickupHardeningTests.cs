using System.Buffers.Binary;
using Microsoft.Xna.Framework;
using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaNpcCoinPickupHardeningTests
{
    [Test]
    public void NormalWorldRejectsNpcCoinExtraValueRequest()
    {
        bool previousExpert = global::Terraria.Main.expertMode;
        bool previousMaster = global::Terraria.Main.masterMode;
        int previousWidth = global::Terraria.Main.maxTilesX;
        int previousHeight = global::Terraria.Main.maxTilesY;
        try
        {
            global::Terraria.Main.expertMode = false;
            global::Terraria.Main.masterMode = false;
            global::Terraria.Main.maxTilesX = 100;
            global::Terraria.Main.maxTilesY = 50;

            PacketProcessingDecision decision = Process(BuildPayload(3, 70, 16, 16));

            Assert.That(
                decision,
                Is.EqualTo(PacketProcessingDecision.Reject("packet.npc_coin_pickup.world_mode_not_supported")));
        }
        finally
        {
            global::Terraria.Main.expertMode = previousExpert;
            global::Terraria.Main.masterMode = previousMaster;
            global::Terraria.Main.maxTilesX = previousWidth;
            global::Terraria.Main.maxTilesY = previousHeight;
        }
    }

    [Test]
    public void ExpertWorldRejectsCoinPositionTooFarFromLiveNpc()
    {
        const short slot = 3;
        bool previousExpert = global::Terraria.Main.expertMode;
        bool previousMaster = global::Terraria.Main.masterMode;
        int previousWidth = global::Terraria.Main.maxTilesX;
        int previousHeight = global::Terraria.Main.maxTilesY;
        global::Terraria.NPC? previousNpc = global::Terraria.Main.npc[slot];
        try
        {
            global::Terraria.Main.expertMode = true;
            global::Terraria.Main.masterMode = false;
            global::Terraria.Main.maxTilesX = 100;
            global::Terraria.Main.maxTilesY = 50;
            global::Terraria.Main.npc[slot] = new global::Terraria.NPC { position = Vector2.Zero };

            PacketProcessingDecision decision = Process(BuildPayload(slot, 70, 100, 100));

            Assert.That(
                decision,
                Is.EqualTo(PacketProcessingDecision.Reject("packet.npc_coin_pickup.too_far_from_npc")));
        }
        finally
        {
            global::Terraria.Main.expertMode = previousExpert;
            global::Terraria.Main.masterMode = previousMaster;
            global::Terraria.Main.maxTilesX = previousWidth;
            global::Terraria.Main.maxTilesY = previousHeight;
            global::Terraria.Main.npc[slot] = previousNpc!;
        }
    }

    private static PacketProcessingDecision Process(byte[] payload)
    {
        var context = new PacketProcessingContext<PacketView>(
            PacketView.FromPayload(PacketTypes.SyncExtraValue, payload),
            PacketDirection.Inbound,
            new ConnectionId(7),
            new PlayerId(7),
            DateTimeOffset.UtcNow);
        return new TerrariaNpcGenerationPolicy().Process(in context);
    }

    private static byte[] BuildPayload(short npcIndex, int extraValue, float x, float y)
    {
        byte[] payload = new byte[14];
        BinaryPrimitives.WriteInt16LittleEndian(payload, npcIndex);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(2), extraValue);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(10), BitConverter.SingleToInt32Bits(y));
        return payload;
    }
}
