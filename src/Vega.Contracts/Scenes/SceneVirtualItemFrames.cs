using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

public readonly record struct VirtualItemFrameId(Guid Value)
{
    public static VirtualItemFrameId New() => new(Guid.NewGuid());
}

/// <summary>Provider-owned, read-only item-frame presentation, never a native entity registration.</summary>
public sealed record SceneVirtualItemFrameDefinition
{
    // Pinned vanilla item-frame tile type (validated against native TileID in the adapter).
    public const ushort TileType = 395;

    public SceneVirtualItemFrameDefinition(VirtualItemFrameId id, int localX, int localY, int itemType, int stack, byte prefix, long revision)
    {
        if (id.Value == Guid.Empty || localX < 0 || localY < 0 || revision < 1 || itemType is < 0 or > ushort.MaxValue ||
            stack is < 0 or > 9999 || (itemType == 0 ? stack != 0 || prefix != 0 : stack == 0))
            throw new ArgumentException("Virtual item frame identity, position, revision or item is invalid.");
        Id = id;
        LocalX = localX;
        LocalY = localY;
        ItemType = itemType;
        Stack = stack;
        Prefix = prefix;
        Revision = revision;
    }

    public VirtualItemFrameId Id { get; }
    public int LocalX { get; }
    public int LocalY { get; }
    public int ItemType { get; }
    public int Stack { get; }
    public byte Prefix { get; }
    public long Revision { get; }
}

public readonly record struct SceneVirtualItemFrameSnapshot(SceneId SceneId, OverlayId OverlayId, WorldId WorldId,
    WorldTilePosition Position, SceneVirtualItemFrameDefinition Definition);
