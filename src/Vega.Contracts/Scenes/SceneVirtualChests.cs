using Vega.Contracts.Worlds;
using Vega.Contracts.Players;

namespace Vega.Contracts.Scenes;

public readonly record struct VirtualChestId(Guid Value)
{
    public static VirtualChestId New() => new(Guid.NewGuid());
}

/// <summary>Immutable, plugin-owned logical chest; coordinates are relative to its tile provider.</summary>
public sealed record SceneVirtualChestDefinition
{
    public const int SlotCount = 40;

    public SceneVirtualChestDefinition(VirtualChestId id, int localX, int localY, string name, long revision,
        IReadOnlyList<ChestItemSnapshot> items)
    {
        if (id.Value == Guid.Empty || localX < 0 || localY < 0 || revision < 1)
            throw new ArgumentException("Virtual chest identity, local position and revision must be valid.");
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(items);
        if (name.Length > 64 || name.Any(char.IsControl) || items.Count > SlotCount)
            throw new ArgumentException("Virtual chest name or item count exceeds its limit.");
        var copy = items.ToArray();
        if (copy.Length > SlotCount) throw new ArgumentException("Too many virtual chest items.", nameof(items));
        var slots = new HashSet<int>();
        foreach (var item in copy)
        {
            if ((uint)item.Slot >= SlotCount || !slots.Add(item.Slot) || item.ItemType is < 0 or > ushort.MaxValue ||
                item.Stack is < 0 or > 9999 || (item.ItemType == 0 ? item.Stack != 0 || item.Prefix != 0 : item.Stack == 0))
                throw new ArgumentException("Virtual chest items must have unique valid slots and consistent stacks.", nameof(items));
        }
        Id = id;
        LocalX = localX;
        LocalY = localY;
        Name = name.Trim();
        Revision = revision;
        Items = Array.AsReadOnly(copy);
    }

    public VirtualChestId Id { get; }
    public int LocalX { get; }
    public int LocalY { get; }
    public string Name { get; }
    public long Revision { get; }
    public IReadOnlyList<ChestItemSnapshot> Items { get; }
}

public readonly record struct SceneVirtualChestSnapshot(SceneId SceneId, OverlayId OverlayId, ChestIdentity Identity,
    SceneVirtualChestDefinition Definition);

/// <summary>One observed interaction, not a native chest-index assignment or item transfer.</summary>
public readonly record struct SceneVirtualChestOpenRequest(SceneVirtualChestSnapshot Chest, SceneSessionObserver Observer,
    DateTimeOffset RequestedAt);

public enum VirtualChestTransferDirection { Withdraw, Deposit }

/// <summary>One explicit server-owned stack transfer following a visible chest interaction.</summary>
public readonly record struct SceneVirtualChestTransferRequest(SceneVirtualChestOpenRequest Open, long InventoryRevision,
    int ChestSlot, int InventorySlot, int Amount, VirtualChestTransferDirection Direction);

public enum VirtualChestTransferStatus { Applied, Unavailable, StaleInteraction, RevisionConflict, InvalidItem, InvalidRequest, AuthorityRequired }

public readonly record struct SceneVirtualChestTransferResult(VirtualChestTransferStatus Status,
    SceneVirtualChestDefinition? Chest = null, long InventoryRevision = 0, string? Error = null);

/// <summary>Host capability; commits native inventory and scene changes on the authoritative game thread.</summary>
public interface IVirtualChestInventoryTransfer
{
    SceneVirtualChestTransferResult TransferVirtualChest(ISceneRuntime scenes, SceneVirtualChestTransferRequest request);
}

/// <summary>Host-only transaction boundary. The inventory assignment must have been prepared and cannot call external code.</summary>
public interface IVirtualChestTransferAuthority
{
    bool TryCommitVirtualChestTransfer(PlayerSnapshot player, SceneVirtualChestOpenRequest open,
        SceneVirtualChestDefinition replacement, Action commitInventory);
}
