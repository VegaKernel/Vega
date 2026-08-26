using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects malformed or out-of-range client-authored NPC interactions before Terraria indexes NPC/world state.
/// Live NPC generation/active-state checks remain in Vega.Terraria where the native NPC array is available.
/// </summary>
public sealed class NpcPacketSanityPolicy(
    int npcCount,
    int npcTypeCount,
    int buffTypeCount,
    int playerCount,
    Func<int> worldWidth,
    Func<int> worldHeight) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.NpcStrike => ValidateStrike(new NpcStrikeView(context.Packet)),
            PacketTypes.NpcTalk => ValidateTalk(new NpcTalkView(context.Packet)),
            PacketTypes.NpcAddBuff => ValidateNpcAddBuff(context.Packet.PayloadSpan),
            PacketTypes.UpdateNPCHome => ValidateHome(new UpdateNPCHomeView(context.Packet)),
            PacketTypes.CatchNPC => ValidateNpcId(new CatchNPCView(context.Packet).NpcId, "packet.npc.catch_id_out_of_range"),
            PacketTypes.ReleaseNPC => ValidateRelease(new ReleaseNPCView(context.Packet)),
            PacketTypes.FishOutNPC => ValidateFishOut(new FishOutNPCView(context.Packet)),
            PacketTypes.TamperWithNPC => ValidateTamper(new TamperWithNPCView(context.Packet)),
            PacketTypes.NpcHurtByDebuff => ValidateDebuffDamage(new NpcHurtByDebuffView(context.Packet)),
            PacketTypes.RequestNPCBuffRemoval => ValidateBuffRemoval(new RequestNPCBuffRemovalView(context.Packet)),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateStrike(NpcStrikeView value)
    {
        PacketProcessingDecision id = ValidateNpcId(value.NpcSlot, "packet.npc.strike_id_out_of_range");
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        if (value.Damage < 0)
        {
            return PacketProcessingDecision.Violation("packet.npc.strike_damage_out_of_range");
        }

        return !float.IsFinite(value.Knockback)
            ? PacketProcessingDecision.Violation("packet.npc.strike_non_finite_knockback")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateTalk(NpcTalkView value) =>
        value.NpcTalkTarget == -1
            ? PacketProcessingDecision.Allow
            : ValidateNpcId(value.NpcTalkTarget, "packet.npc.talk_id_out_of_range");

    private PacketProcessingDecision ValidateNpcAddBuff(ReadOnlySpan<byte> payload)
    {
        // Multiplicity 2.6.2 models Buff as byte. Protocol 326 uses UInt16.
        if (payload.Length != 6)
        {
            return PacketProcessingDecision.Reject("packet.npc.buff_malformed");
        }

        short npcId = BinaryPrimitives.ReadInt16LittleEndian(payload);
        ushort buffId = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        short time = BinaryPrimitives.ReadInt16LittleEndian(payload[4..]);

        PacketProcessingDecision id = ValidateNpcId(npcId, "packet.npc.buff_id_out_of_range");
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        if (buffId >= buffTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.npc.buff_type_out_of_range");
        }

        return time < 0
            ? PacketProcessingDecision.Violation("packet.npc.buff_time_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateHome(UpdateNPCHomeView value)
    {
        PacketProcessingDecision id = ValidateNpcId(value.NpcId, "packet.npc.home_id_out_of_range");
        return id.Kind != PacketDecisionKind.Allow
            ? id
            : ValidateTileCoordinates(value.HomeTileX, value.HomeTileY, "packet.npc.home_position_out_of_world");
    }

    private PacketProcessingDecision ValidateRelease(ReleaseNPCView value)
    {
        if (value.NpcType < 0 || value.NpcType >= npcTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.npc.release_type_out_of_range");
        }

        int widthPixels = checked(worldWidth() * 16);
        int heightPixels = checked(worldHeight() * 16);
        return value.X < 0 || value.Y < 0 || value.X >= widthPixels || value.Y >= heightPixels
            ? PacketProcessingDecision.Violation("packet.npc.release_position_out_of_world")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateFishOut(FishOutNPCView value)
    {
        PacketProcessingDecision position = ValidateTileCoordinates(
            value.TileX,
            value.TileY,
            "packet.npc.fish_position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        return value.NpcNetId < 0 || value.NpcNetId >= npcTypeCount
            ? PacketProcessingDecision.Violation("packet.npc.fish_type_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateTamper(TamperWithNPCView value)
    {
        PacketProcessingDecision id = ValidateNpcId(value.NpcId, "packet.npc.tamper_id_out_of_range");
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        if (!Enum.IsDefined(typeof(TamperWithNPCActionType), value.ActionType))
        {
            return PacketProcessingDecision.Violation("packet.npc.tamper_action_out_of_range");
        }

        if (!value.HasImmunityPayload)
        {
            return PacketProcessingDecision.Allow;
        }

        if (value.ImmunityTime < 0)
        {
            return PacketProcessingDecision.Violation("packet.npc.tamper_immunity_out_of_range");
        }

        return value.PlayerId < -1 || value.PlayerId >= playerCount
            ? PacketProcessingDecision.Violation("packet.npc.tamper_player_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateDebuffDamage(NpcHurtByDebuffView value)
    {
        PacketProcessingDecision id = ValidateNpcId(value.NpcId, "packet.npc.debuff_id_out_of_range");
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        return value.Damage < 0
            ? PacketProcessingDecision.Violation("packet.npc.debuff_damage_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateBuffRemoval(RequestNPCBuffRemovalView value)
    {
        PacketProcessingDecision id = ValidateNpcId(value.NpcId, "packet.npc.buff_remove_id_out_of_range");
        if (id.Kind != PacketDecisionKind.Allow)
        {
            return id;
        }

        return value.BuffId >= buffTypeCount
            ? PacketProcessingDecision.Violation("packet.npc.buff_remove_type_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateNpcId(int id, string reason) =>
        id < 0 || id >= npcCount
            ? PacketProcessingDecision.Violation(reason)
            : PacketProcessingDecision.Allow;

    private PacketProcessingDecision ValidateTileCoordinates(int x, int y, string reason)
    {
        int width = worldWidth();
        int height = worldHeight();
        return width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height
            ? PacketProcessingDecision.Violation(reason)
            : PacketProcessingDecision.Allow;
    }
}
