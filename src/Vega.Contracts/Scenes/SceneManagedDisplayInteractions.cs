using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

public enum ManagedDisplayKind { ItemFrame, WeaponsRack }
public enum ManagedDisplayTransferStatus { Applied, Unavailable, StaleInteraction, RevisionConflict, InvalidItem, AuthorityRequired }

public readonly record struct SceneManagedDisplayTarget(SceneId SceneId, OverlayId OverlayId, WorldId WorldId,
    WorldTilePosition Position, Guid EntityId, ManagedDisplayKind Kind, long Revision);

/// <summary>A complete, conserved placement into an empty provider-owned display, awaiting plugin approval.</summary>
public readonly record struct SceneManagedDisplayTransferIntent(Guid Id, SceneManagedDisplayTarget Target, SceneSessionObserver Observer,
    long InventoryRevision, int InventorySlot, SceneDisplayItem Item, DateTimeOffset RequestedAt);

public readonly record struct SceneManagedDisplayTransferResult(ManagedDisplayTransferStatus Status,
    SceneVirtualItemFrameDefinition? ItemFrame = null, SceneVirtualDisplayDefinition? Display = null, long InventoryRevision = 0, string? Error = null);

public interface IManagedDisplayInventoryHost
{
    bool TryArmManagedDisplay(ISceneRuntime scenes, PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind);
    SceneManagedDisplayTransferResult AcceptManagedDisplayTransfer(ISceneRuntime scenes, SceneManagedDisplayTransferIntent intent);
}

/// <summary>Host-only managed-inventory transaction authority. Does not expose native tile-entity indices.</summary>
public interface IManagedDisplayAuthority
{
    bool TryResolveManagedDisplayTarget(PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind,
        out SceneManagedDisplayTarget target);
    bool TryArmManagedDisplay(PlayerSnapshot player, SceneManagedDisplayTarget target, PlayerInventorySnapshot inventory, IVirtualChestGuiHost host);
    bool ObserveManagedDisplayPlacement(PlayerSnapshot player, ManagedDisplayKind kind, int x, int y, SceneDisplayItem proposed, DateTimeOffset now);
    bool TryTakeManagedDisplayTransfer(SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind, out SceneManagedDisplayTransferIntent? intent);
    bool TryClaimManagedDisplayTransfer(SceneManagedDisplayTransferIntent intent);
    bool TryCommitManagedDisplayTransfer(PlayerSnapshot player, SceneManagedDisplayTransferIntent intent, Action commitInventory,
        out SceneManagedDisplayTransferResult result);
    bool RejectManagedDisplayTransfer(SceneManagedDisplayTransferIntent intent);
    bool CancelManagedDisplay(PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind);
}
