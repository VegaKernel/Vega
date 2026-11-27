using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

public readonly record struct VirtualDisplayId(Guid Value)
{
    public static VirtualDisplayId New() => new(Guid.NewGuid());
}

public enum SceneVirtualDisplayKind { WeaponsRack, DisplayDoll, HatRack, FoodPlatter }

public readonly record struct SceneDisplayItem(int ItemType, int Stack, byte Prefix = 0)
{
    internal bool IsValid => ItemType is >= 0 and <= ushort.MaxValue && Stack is >= 0 and <= 9999 &&
        (ItemType == 0 ? Stack == 0 && Prefix == 0 : Stack > 0);
}

/// <summary>Read-only visual furniture metadata; the provider owns payload and revision, never native registration.</summary>
public sealed record SceneVirtualDisplayDefinition
{
    public SceneVirtualDisplayDefinition(VirtualDisplayId id, SceneVirtualDisplayKind kind, int localX, int localY,
        IReadOnlyList<SceneDisplayItem> items, IReadOnlyList<SceneDisplayItem> dyes, long revision)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(dyes);
        if (id.Value == Guid.Empty || localX < 0 || localY < 0 || revision < 1 || !Enum.IsDefined(kind))
            throw new ArgumentException("Virtual display identity, shape or revision is invalid.");
        int capacity = kind == SceneVirtualDisplayKind.DisplayDoll ? 9 : kind == SceneVirtualDisplayKind.HatRack ? 2 : 1;
        int dyeCapacity = kind is SceneVirtualDisplayKind.DisplayDoll or SceneVirtualDisplayKind.HatRack ? capacity : 0;
        if (items.Count > capacity || dyes.Count > dyeCapacity) throw new ArgumentException("Virtual display slot count exceeds native capacity.");
        var itemCopy = items.ToArray();
        var dyeCopy = dyes.ToArray();
        if (itemCopy.Length > capacity || dyeCopy.Length > dyeCapacity || itemCopy.Any(item => !item.IsValid) || dyeCopy.Any(item => !item.IsValid))
            throw new ArgumentException("Virtual display items must contain valid stacks.");
        Id = id;
        Kind = kind;
        LocalX = localX;
        LocalY = localY;
        Items = Array.AsReadOnly(itemCopy);
        Dyes = Array.AsReadOnly(dyeCopy);
        Revision = revision;
    }
    public VirtualDisplayId Id { get; }
    public SceneVirtualDisplayKind Kind { get; }
    public int LocalX { get; }
    public int LocalY { get; }
    public IReadOnlyList<SceneDisplayItem> Items { get; }
    public IReadOnlyList<SceneDisplayItem> Dyes { get; }
    public long Revision { get; }
    public ushort TileType => Kind switch { SceneVirtualDisplayKind.WeaponsRack => 471, SceneVirtualDisplayKind.DisplayDoll => 470,
        SceneVirtualDisplayKind.HatRack => 475, SceneVirtualDisplayKind.FoodPlatter => 520, _ => throw new InvalidOperationException() };
    public int Width => Kind switch { SceneVirtualDisplayKind.WeaponsRack or SceneVirtualDisplayKind.HatRack => 3,
        SceneVirtualDisplayKind.DisplayDoll => 2, _ => 1 };
    public int Height => Kind switch { SceneVirtualDisplayKind.WeaponsRack or SceneVirtualDisplayKind.DisplayDoll => 3,
        SceneVirtualDisplayKind.HatRack => 4, _ => 1 };
}

public readonly record struct SceneVirtualDisplaySnapshot(SceneId SceneId, OverlayId OverlayId, WorldId WorldId,
    WorldTilePosition Position, SceneVirtualDisplayDefinition Definition);
