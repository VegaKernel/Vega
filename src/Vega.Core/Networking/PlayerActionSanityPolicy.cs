using System.Buffers.Binary;
using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Enforces protocol-safe ranges for client-authored player actions without trying to infer gameplay cheating.
/// </summary>
public sealed class PlayerActionSanityPolicy(
    int playerCount,
    int npcCount,
    int buffTypeCount,
    int projectileTypeCount,
    Func<int> worldWidth,
    Func<int> worldHeight) : IPacketProcessor<PacketView>
{
    private const byte MaximumVanillaTeam = 5;
    private const byte LoadoutCount = 3;
    private const int TileSize = 16;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.PlayerAnimation => ValidateAnimation(new PlayerAnimationView(context.Packet)),
            PacketTypes.PlayerTeam => ValidateTeam(new PlayerTeamView(context.Packet).Team),
            PacketTypes.TeamChangeFromUI => ValidateTeam(new TeamChangeFromUIView(context.Packet).Team),
            PacketTypes.PlayerBuff => ValidatePlayerBuffList(context.Packet.PayloadSpan),
            PacketTypes.PlayerStealth => ValidateStealth(new PlayerStealthView(context.Packet)),
            PacketTypes.PlayHarp => ValidateHarp(new PlayHarpView(context.Packet)),
            PacketTypes.UpdateMinionTarget => ValidateMinionRestTarget(new UpdateMinionTargetView(context.Packet)),
            PacketTypes.MinionAttackTargetUpdate => ValidateMinionAttackTarget(new MinionAttackTargetUpdateView(context.Packet)),
            PacketTypes.UpdatePlayerLuckFactors => ValidateLuck(new UpdatePlayerLuckFactorsView(context.Packet)),
            PacketTypes.SyncLoadout => ValidateLoadout(new SyncLoadoutView(context.Packet)),
            PacketTypes.PlayerAddBuff => ValidatePlayerAddBuff(context.Packet.PayloadSpan),
            PacketTypes.PlayerHealOther => ValidateHealOther(new PlayerHealOtherView(context.Packet)),
            PacketTypes.LandGolfBallInCup => ValidateGolf(new LandGolfBallInCupView(context.Packet)),
            _ => PacketProcessingDecision.Allow
        };
    }

    private static PacketProcessingDecision ValidateAnimation(PlayerAnimationView value)
    {
        if (!float.IsFinite(value.ItemRotation))
        {
            return PacketProcessingDecision.Violation("packet.player_animation.non_finite_rotation");
        }

        return value.ItemAnimation < 0
            ? PacketProcessingDecision.Violation("packet.player_animation.duration_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateTeam(byte team) =>
        team > MaximumVanillaTeam
            ? PacketProcessingDecision.Violation("packet.player_team.team_out_of_range")
            : PacketProcessingDecision.Allow;

    private PacketProcessingDecision ValidatePlayerBuffList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || ((payload.Length - 1) & 1) != 0)
        {
            return PacketProcessingDecision.Reject("packet.player_buff_list.malformed");
        }

        bool reachedTerminator = false;
        for (int offset = 1; offset < payload.Length; offset += sizeof(ushort))
        {
            ushort buff = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            if (buff == 0)
            {
                reachedTerminator = true;
                continue;
            }

            if (reachedTerminator)
            {
                return PacketProcessingDecision.Violation("packet.player_buff_list.nonzero_after_terminator");
            }

            if (buff >= buffTypeCount)
            {
                return PacketProcessingDecision.Violation("packet.player_buff_list.type_out_of_range");
            }
        }

        return PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateStealth(PlayerStealthView value) =>
        !float.IsFinite(value.Stealth)
            ? PacketProcessingDecision.Violation("packet.player_stealth.non_finite")
            : PacketProcessingDecision.Allow;

    private static PacketProcessingDecision ValidateHarp(PlayHarpView value) =>
        !float.IsFinite(value.Note)
            ? PacketProcessingDecision.Violation("packet.harp.non_finite_note")
            : PacketProcessingDecision.Allow;

    private PacketProcessingDecision ValidateMinionRestTarget(UpdateMinionTargetView value)
    {
        if (!float.IsFinite(value.TargetX) || !float.IsFinite(value.TargetY))
        {
            return PacketProcessingDecision.Violation("packet.minion.non_finite_target");
        }

        int width = worldWidth();
        int height = worldHeight();
        return width > 0 && height > 0 &&
            (value.TargetX < 0 || value.TargetY < 0 ||
             value.TargetX > width * TileSize || value.TargetY > height * TileSize)
            ? PacketProcessingDecision.Violation("packet.minion.target_out_of_world")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateMinionAttackTarget(MinionAttackTargetUpdateView value) =>
        value.MinionAttackTarget == -1 ||
        value.MinionAttackTarget >= 0 && value.MinionAttackTarget < npcCount
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Violation("packet.minion.attack_target_out_of_range");

    private static PacketProcessingDecision ValidateLuck(UpdatePlayerLuckFactorsView value)
    {
        if (value.LadyBugLuckTimeLeft < 0)
        {
            return PacketProcessingDecision.Violation("packet.player_luck.timer_out_of_range");
        }

        return !float.IsFinite(value.TorchLuck) ||
               !float.IsFinite(value.EquipmentBasedLuckBonus) ||
               !float.IsFinite(value.CoinLuck)
            ? PacketProcessingDecision.Violation("packet.player_luck.non_finite")
            : PacketProcessingDecision.Allow;
    }

    private static PacketProcessingDecision ValidateLoadout(SyncLoadoutView value) =>
        value.LoadoutIndex >= LoadoutCount
            ? PacketProcessingDecision.Violation("packet.loadout.index_out_of_range")
            : PacketProcessingDecision.Allow;

    private PacketProcessingDecision ValidatePlayerAddBuff(ReadOnlySpan<byte> payload)
    {
        // Protocol 325 encodes the target as UInt8, the buff id as UInt16 and the duration as Int32.
        if (payload.Length != 7)
        {
            return PacketProcessingDecision.Reject("packet.player_buff_add.malformed");
        }

        byte target = payload[0];
        ushort buff = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        int time = BinaryPrimitives.ReadInt32LittleEndian(payload[3..]);

        if (target >= playerCount)
        {
            return PacketProcessingDecision.Violation("packet.player_buff_add.target_out_of_range");
        }

        if (buff >= buffTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.player_buff_add.type_out_of_range");
        }

        return time < 0
            ? PacketProcessingDecision.Violation("packet.player_buff_add.time_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateHealOther(PlayerHealOtherView value)
    {
        if (value.PlayerId >= playerCount)
        {
            return PacketProcessingDecision.Violation("packet.player_heal.target_out_of_range");
        }

        return value.HealAmount < 0
            ? PacketProcessingDecision.Violation("packet.player_heal.amount_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateGolf(LandGolfBallInCupView value)
    {
        int width = worldWidth();
        int height = worldHeight();
        if (width <= 0 || height <= 0 || value.CupX >= width || value.CupY >= height)
        {
            return PacketProcessingDecision.Violation("packet.golf.cup_out_of_world");
        }

        return value.ProjectileType >= projectileTypeCount
            ? PacketProcessingDecision.Violation("packet.golf.projectile_type_out_of_range")
            : PacketProcessingDecision.Allow;
    }
}
