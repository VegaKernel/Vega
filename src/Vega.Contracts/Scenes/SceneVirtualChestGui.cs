using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

/// <summary>A complete balanced GUI movement, awaiting explicit plugin approval.</summary>
public readonly record struct SceneVirtualChestGuiTransferIntent(Guid Id, SceneVirtualChestTransferRequest Transfer);

/// <summary>Native managed-inventory GUI capability. A session stays managed until disconnect.</summary>
public interface IVirtualChestGuiHost
{
    bool TryPresentManagedGui(ISceneRuntime scenes, SceneVirtualChestOpenRequest open);
    SceneVirtualChestTransferResult AcceptManagedGuiTransfer(ISceneRuntime scenes, SceneVirtualChestGuiTransferIntent intent);
    bool SynchronizeManagedGui(PlayerSnapshot player, short index, ChestIdentity? closedChest, IReadOnlyList<int> inventoryCorrections);
}

/// <summary>Host-only authority for the bounded GUI session and its explicit transaction intents.</summary>
public interface IVirtualChestGuiAuthority
{
    bool HasManagedChestPlayers { get; }
    bool IsManagedChestSlot(WorldId world, PlayerId player);
    bool CanPresentManagedGui(PlayerSnapshot player, ChestIdentity chest);
    bool CanUseManagedGuiIndex(PlayerSnapshot player, short index, ChestIdentity chest);
    bool IsManagedChestPlayer(PlayerSnapshot player);
    bool TryBeginManagedGui(PlayerSnapshot player, SceneVirtualChestOpenRequest open, short index,
        PlayerInventorySnapshot inventory, IVirtualChestGuiHost host);
    bool ObserveManagedInventory(PlayerSnapshot player, InventorySlotReplacement proposed, DateTimeOffset now);
    bool ObserveManagedChest(PlayerSnapshot player, short index, ChestItemSnapshot proposed, DateTimeOffset now);
    bool ObserveManagedChestOpen(PlayerSnapshot player, short index, int x, int y);
    bool RequestManagedGuiClose(PlayerSnapshot player);
    bool RequestManagedGuiClose(PlayerSnapshot player, SceneId scene, OverlayId overlay, VirtualChestId chest);
    bool TryTakeManagedGuiTransfer(SceneId scene, OverlayId overlay, VirtualChestId chest, out SceneVirtualChestGuiTransferIntent? intent);
    bool TryClaimManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent, out SceneVirtualChestTransferRequest transfer);
    bool RejectManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent);
    bool CompleteManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent, SceneVirtualChestTransferResult result,
        PlayerInventorySnapshot? inventory, out short index, out SceneVirtualChestSnapshot? chest);
    void TickManagedChestGuis(IPlayerDirectory players, DateTimeOffset now);
}
