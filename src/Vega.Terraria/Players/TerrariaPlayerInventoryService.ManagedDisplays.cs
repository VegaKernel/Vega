using Terraria.DataStructures;
using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerInventoryService : IManagedDisplayInventoryHost
{
    public bool TryArmManagedDisplay(ISceneRuntime scenes, PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind)
    {
        if (player.SessionId is not { } session || scenes is not IManagedDisplayAuthority authority ||
            !TryGetGuiPlayer(new(player.Id,session),player.WorldId,out var expected,out var live) || live.dead || live.chest >= 0 ||
            !authority.TryResolveManagedDisplayTarget(expected,scene,overlay,entity,kind,out var target) || !NativeDisplayPositionAvailable(target) ||
            Capture(expected) is not { } inventory) return false;
        return authority.TryArmManagedDisplay(expected,target,inventory,this);
    }

    public SceneManagedDisplayTransferResult AcceptManagedDisplayTransfer(ISceneRuntime scenes, SceneManagedDisplayTransferIntent intent)
    {
        if (!dispatcher.IsGameThread || scenes is not IManagedDisplayAuthority authority) return new(ManagedDisplayTransferStatus.Unavailable);
        if (!global::Terraria.Main.ServerSideCharacter) return new(ManagedDisplayTransferStatus.AuthorityRequired);
        if (!authority.TryClaimManagedDisplayTransfer(intent)) return new(ManagedDisplayTransferStatus.StaleInteraction);
        SceneManagedDisplayTransferResult Reject(ManagedDisplayTransferStatus status)
        { authority.RejectManagedDisplayTransfer(intent); return new(status); }
        if (!TryGetGuiPlayer(intent.Observer,intent.Target.WorldId,out var expected,out var player) || player.dead || !NativeDisplayPositionAvailable(intent.Target))
            return Reject(ManagedDisplayTransferStatus.StaleInteraction);
        long revision = RefreshRevision(expected,player);
        if (revision != intent.InventoryRevision) return Reject(ManagedDisplayTransferStatus.RevisionConflict);
        int slot = intent.InventorySlot;
        var item = intent.Item;
        if ((uint)slot >= (uint)player.inventory.Length || (slot >= 50 && slot != PlayerItemSlotID.InventoryMouseItem) ||
            item.ItemType <= 0 || item.ItemType >= ItemID.Count || item.Prefix >= PrefixID.Count || item.Stack <= 0)
            return Reject(ManagedDisplayTransferStatus.InvalidItem);
        var source = player.inventory[slot];
        if (source.type != item.ItemType || source.prefix != item.Prefix || source.stack < item.Stack || source.favorited)
            return Reject(ManagedDisplayTransferStatus.InvalidItem);
        var prototype = new global::Terraria.Item();
        prototype.SetDefaults(item.ItemType);
        if (prototype.type != item.ItemType || item.Stack > prototype.maxStack || item.Prefix != 0 && !prototype.Prefix(item.Prefix) ||
            intent.Target.Kind == ManagedDisplayKind.WeaponsRack && (item.Stack != 1 || prototype.damage <= 0))
            return Reject(ManagedDisplayTransferStatus.InvalidItem);
        var next = source.Clone();
        next.stack -= item.Stack;
        if (next.stack == 0) next.TurnToAir();
        if (!authority.TryCommitManagedDisplayTransfer(expected,intent,() => player.inventory[slot] = next,out var result))
            return Reject(ManagedDisplayTransferStatus.StaleInteraction);
        fingerprints[expected.Id.Slot] = Fingerprint(player);
        revision = ++revisions[expected.Id.Slot];
        try
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncEquipment,expected.Id.Slot,-1,null,expected.Id.Slot,slot,next.prefix);
            global::Terraria.NetMessage.SendData(MessageID.SyncEquipment,-1,expected.Id.Slot,null,expected.Id.Slot,slot,next.prefix);
        }
        catch (Exception)
        { return result with { InventoryRevision = revision, Error = "inventory.sync_failed" }; }
        return result with { InventoryRevision = revision };
    }

    private static bool NativeDisplayPositionAvailable(SceneManagedDisplayTarget target)
    {
        int x = target.Position.X, y = target.Position.Y;
        int size = target.Kind == ManagedDisplayKind.ItemFrame ? 2 : 3;
        return x >= 0 && y >= 0 && x <= short.MaxValue-size && y <= short.MaxValue-size &&
            x <= global::Terraria.Main.maxTilesX-size && y <= global::Terraria.Main.maxTilesY-size && !TileEntity.ByPosition.ContainsKey(new Point16(x,y));
    }
}
