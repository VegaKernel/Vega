using Multiplicity.Packets;
using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>Opt-in direct gameplay admission, distinct from malformed-packet security and native synchronization.</summary>
internal sealed class NativeGameplayReadinessHooks(
    IPlayerDirectory players,
    Func<PlayerSnapshot, bool> readiness,
    IOperationsLogger logger,
    bool authoritativeCharacters = false) : IDisposable
{
    private bool attached;
    private int failureLogged;

    public void Attach()
    {
        if (attached) return;
        Hooks.MessageBuffer.GetData += OnReceiveData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached) return;
        Hooks.MessageBuffer.GetData -= OnReceiveData;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        int slot = args.Instance.whoAmI;
        if (args.Result == HookResult.Cancel || (uint)slot >= (uint)global::Terraria.Netplay.Clients.Length) return;
        var state = (TerrariaConnectionState)(global::Terraria.Netplay.Clients[slot]?.State ?? 0);
        var packet = (PacketTypes)args.PacketId;
        if (!ShouldApply(state, packet) && !(authoritativeCharacters && ShouldApplyPendingCharacterSync(state, packet))) return;
        try
        {
            if (players.TryGet(new PlayerId(slot), out var player) && player is not null && readiness(player)) return;
        }
        catch (Exception ex)
        {
            // A readiness check must not strand vanilla handshake or spam the log
            // with ordinary denied gameplay traffic while the character is loading.
            if (Interlocked.Exchange(ref failureLogged, 1) == 0)
                logger.Log(OperationsLogLevel.Error, "character-readiness", "Gameplay readiness check failed; direct gameplay remains blocked.", ex);
        }
        args.Result = HookResult.Cancel;
    }

    internal static bool IsFullyConnected(int slot) => (uint)slot < (uint)global::Terraria.Netplay.Clients.Length &&
        global::Terraria.Netplay.Clients[slot]?.State == (int)TerrariaConnectionState.FullyConnected;

    internal static bool ShouldApplyPendingCharacterSync(TerrariaConnectionState state, PacketTypes packetType) =>
        state >= TerrariaConnectionState.WorldRequested && state <= TerrariaConnectionState.FullyConnected &&
        packetType is PacketTypes.PlayerSlot or PacketTypes.SyncLoadout or PacketTypes.PlayerHp or PacketTypes.PlayerMana or
            PacketTypes.PlayerInfo or PacketTypes.PlayerBuff or PacketTypes.NumberOfAnglerQuestsCompleted;

    internal static bool ShouldApply(TerrariaConnectionState state, PacketTypes packetType)
    {
        if (state != TerrariaConnectionState.FullyConnected) return false;

        // These synchronize native movement, character values, inventory and UI
        // rather than establish complete server-side character authority here.
        // Preserve inventory/health/respawn sync and chat for the vanilla client.
        if (packetType is PacketTypes.SyncPlayerChestIndex or PacketTypes.ChestOpen) return false;
        PacketFamily family = PacketPipelineClassification.Get(packetType).Family;
        if (family is PacketFamily.Item or PacketFamily.Projectile or PacketFamily.WorldInteraction or PacketFamily.Chest or PacketFamily.TileEntity)
            return true;

        return packetType is PacketTypes.NpcStrike or PacketTypes.NpcItemStrike or PacketTypes.NpcTalk or PacketTypes.NpcSpecial or
            PacketTypes.CatchNPC or PacketTypes.ReleaseNPC or PacketTypes.CompleteAnglerQuest or PacketTypes.NpcShopItem or
            PacketTypes.HitSwitch or PacketTypes.SpawnBossorInvasion or PacketTypes.CrystalInvasionStart or PacketTypes.ToggleParty or
            PacketTypes.WiredCannonShot or PacketTypes.WeaponsRackTryPlacing or PacketTypes.ItemOwner or PacketTypes.TogglePvp or
            PacketTypes.PlayerTeam or PacketTypes.TeamChangeFromUI or PacketTypes.TeleportationPotion or PacketTypes.PlayerTeleportPortal or
            PacketTypes.PlayerHealOther;
    }
}
