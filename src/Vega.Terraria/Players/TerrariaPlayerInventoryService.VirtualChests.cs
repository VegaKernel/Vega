using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerInventoryService : IVirtualChestInventoryTransfer
{
    public SceneVirtualChestTransferResult TransferVirtualChest(ISceneRuntime scenes, SceneVirtualChestTransferRequest request)
        => TransferVirtualChestCore(scenes, request, allowMouse: false);

    private SceneVirtualChestTransferResult TransferVirtualChestCore(ISceneRuntime scenes, SceneVirtualChestTransferRequest request, bool allowMouse)
    {
        if (!dispatcher.IsGameThread || scenes is not IVirtualChestTransferAuthority authority)
            return new(VirtualChestTransferStatus.Unavailable);
        if (!global::Terraria.Main.ServerSideCharacter)
            return new(VirtualChestTransferStatus.AuthorityRequired, Error: "inventory.native_ssc_required");
        // Main inventory only: no mouse item, trash, coin or ammo slots.
        if (((uint)request.InventorySlot >= 50 && (!allowMouse || request.InventorySlot != PlayerItemSlotID.InventoryMouseItem)) || (uint)request.ChestSlot >= SceneVirtualChestDefinition.SlotCount ||
            request.Amount is < 1 or > 9999 || !Enum.IsDefined(request.Direction) || request.Open.Chest.Definition is not { } chest)
            return new(VirtualChestTransferStatus.InvalidRequest);
        var observer = request.Open.Observer;
        if (!players.TryGet(observer.Player, out var expected) || expected is null || expected.SessionId != observer.Session ||
            expected.WorldId != request.Open.Chest.Identity.WorldId || !players.TryGetSessionPlayer(expected, out var player) || player.dead)
            return new(VirtualChestTransferStatus.StaleInteraction);
        // The published directory can lag behind current native movement within a tick.
        expected = expected with { Position = new(player.position.X, player.position.Y), IsDead = player.dead };
        long revision = RefreshRevision(expected, player);
        if (revision != request.InventoryRevision) return new(VirtualChestTransferStatus.RevisionConflict, InventoryRevision: revision);

        if ((uint)request.InventorySlot >= (uint)player.inventory.Length) return new(VirtualChestTransferStatus.InvalidRequest);
        var inventoryItem = player.inventory[request.InventorySlot];
        if (PrepareVirtualChestTransfer(request, chest, inventoryItem) is not { } prepared)
            return new(VirtualChestTransferStatus.InvalidItem);
        var (replacement, nextInventory) = prepared;
        if (!authority.TryCommitVirtualChestTransfer(expected, request.Open, replacement, () => player.inventory[request.InventorySlot] = nextInventory))
            return new(VirtualChestTransferStatus.StaleInteraction);

        fingerprints[expected.Id.Slot] = Fingerprint(player);
        revision = ++revisions[expected.Id.Slot];
        try
        {
            global::Terraria.NetMessage.SendData(MessageID.SyncEquipment, expected.Id.Slot, -1, null, expected.Id.Slot, request.InventorySlot, nextInventory.prefix);
            global::Terraria.NetMessage.SendData(MessageID.SyncEquipment, -1, expected.Id.Slot, null, expected.Id.Slot, request.InventorySlot, nextInventory.prefix);
        }
        catch (Exception)
        {
            // The transaction is already committed; the caller must not retry the transfer as a failure.
            return new(VirtualChestTransferStatus.Applied, replacement, revision, "inventory.sync_failed");
        }
        return new(VirtualChestTransferStatus.Applied, replacement, revision);
    }

    private static (SceneVirtualChestDefinition Chest, global::Terraria.Item Inventory)? PrepareVirtualChestTransfer(
        SceneVirtualChestTransferRequest request, SceneVirtualChestDefinition chest, global::Terraria.Item inventoryItem)
    {
        ChestItemSnapshot chestItem = chest.Items.FirstOrDefault(item => item.Slot == request.ChestSlot);
        bool withdrawing = request.Direction == VirtualChestTransferDirection.Withdraw;
        int type = withdrawing ? chestItem.ItemType : inventoryItem.type;
        int prefix = withdrawing ? chestItem.Prefix : inventoryItem.prefix;
        int sourceStack = withdrawing ? chestItem.Stack : inventoryItem.stack;
        int destinationType = withdrawing ? inventoryItem.type : chestItem.ItemType;
        int destinationPrefix = withdrawing ? inventoryItem.prefix : chestItem.Prefix;
        int destinationStack = withdrawing ? inventoryItem.stack : chestItem.Stack;
        if (type <= 0 || type >= ItemID.Count || prefix >= PrefixID.Count || sourceStack < request.Amount || destinationStack < 0 ||
            (destinationType == 0 && destinationStack != 0) ||
            (!withdrawing && inventoryItem.favorited) || (destinationType != 0 && (destinationType != type || destinationPrefix != prefix)))
            return null;

        var prototype = new global::Terraria.Item();
        prototype.SetDefaults(type);
        if (prefix != 0 && !prototype.Prefix(prefix) || sourceStack > prototype.maxStack ||
            (long)destinationStack + request.Amount > Math.Min(prototype.maxStack, 9999))
            return null;

        int remaining = sourceStack - request.Amount;
        int received = destinationStack + request.Amount;
        global::Terraria.Item nextInventory = inventoryItem.type == 0 ? prototype : inventoryItem.Clone();
        nextInventory.stack = withdrawing ? received : remaining;
        if (nextInventory.stack == 0) nextInventory.TurnToAir();
        int nextChestStack = withdrawing ? remaining : received;
        var nextItems = chest.Items.Where(item => item.Slot != request.ChestSlot).ToList();
        if (nextChestStack != 0) nextItems.Add(new(request.ChestSlot, type, nextChestStack, (byte)prefix));
        var replacement = new SceneVirtualChestDefinition(chest.Id, chest.LocalX, chest.LocalY, chest.Name,
            checked(chest.Revision + 1), nextItems);
        return (replacement, nextInventory);
    }
}
