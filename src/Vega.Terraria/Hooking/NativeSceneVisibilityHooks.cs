using Multiplicity.Packets;
using TerrariaRuntime;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeSceneVisibilityHooks(
    ISceneVisibilityRuntime visibility,
    IPlayerDirectory players,
    WorldId worldId) : IDisposable
{
    [ThreadStatic]
    private static bool forwarding;
    private bool attached;

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.NetMessage.SendData += OnSendData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.NetMessage.SendData -= OnSendData;
        attached = false;
    }

    private void OnSendData(object? sender, Hooks.NetMessage.SendDataEventArgs args)
    {
        if (forwarding || !visibility.HasAssignments ||
            args.Event != HookEvent.Before || args.Result == HookResult.Cancel ||
            !TryResolve((PacketTypes)args.MsgType, out SceneEntityKind kind))
        {
            return;
        }

        var entity = new SceneEntity(kind, args.Number);
        if (args.RemoteClient >= 0)
        {
            if (!visibility.CanObserve(worldId, new PlayerId(args.RemoteClient), entity))
            {
                args.Result = HookResult.Cancel;
            }

            return;
        }

        PlayerId[] recipients = players.SnapshotOnlinePlayers()
            .Where(player => player.WorldId == worldId)
            .Select(static player => player.Id)
            .Where(player => player.Slot != args.IgnoreClient)
            .ToArray();
        PlayerId[] allowed = recipients
            .Where(player => visibility.CanObserve(worldId, player, entity))
            .ToArray();
        if (allowed.Length == recipients.Length)
        {
            return;
        }

        args.Result = HookResult.Cancel;
        forwarding = true;
        try
        {
            foreach (PlayerId recipient in allowed)
            {
                global::Terraria.NetMessage.SendData(
                    args.MsgType,
                    recipient.Slot,
                    -1,
                    args.Text,
                    args.Number,
                    args.Number2,
                    args.Number3,
                    args.Number4,
                    args.Number5,
                    args.Number6,
                    args.Number7);
            }
        }
        finally
        {
            forwarding = false;
        }
    }

    private static bool TryResolve(PacketTypes packet, out SceneEntityKind kind)
    {
        switch (packet)
        {
            case PacketTypes.PlayerInfo:
            case PacketTypes.PlayerSpawn:
            case PacketTypes.PlayerUpdate:
            case PacketTypes.PlayerActive:
            case PacketTypes.PlayerHp:
            case PacketTypes.PlayerAnimation:
            case PacketTypes.PlayerMana:
            case PacketTypes.PlayerTeam:
            case PacketTypes.PlayerBuff:
            case PacketTypes.PlayerHealOther:
            case PacketTypes.PlayerDodge:
            case PacketTypes.PlayerStealth:
            case PacketTypes.TogglePvp:
            case PacketTypes.PlayerHurtV2:
            case PacketTypes.PlayerDeathV2:
                kind = SceneEntityKind.Player;
                return true;
            case PacketTypes.NpcUpdate:
            case PacketTypes.NpcItemStrike:
            case PacketTypes.NpcStrike:
            case PacketTypes.NpcAddBuff:
            case PacketTypes.NpcUpdateBuff:
            case PacketTypes.UpdateNPCName:
            case PacketTypes.UpdateNPCHome:
            case PacketTypes.NpcTeleportPortal:
            case PacketTypes.NpcHurtByDebuff:
                kind = SceneEntityKind.Npc;
                return true;
            case PacketTypes.ProjectileNew:
            case PacketTypes.ProjectileDestroy:
                kind = SceneEntityKind.Projectile;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
