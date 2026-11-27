using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Scenes;

/// <summary>Projects a portable schematic into a non-canonical scene. Supported item-frame records are observer-scoped presentations.</summary>
public static class SchematicSceneProjection
{
    public static SceneOverlayDefinition Create(SchematicDocument document, string name, int x, int y,
        SceneObserverScope observers, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (x < 0 || y < 0) throw new ArgumentOutOfRangeException(nameof(x), "Placement must be non-negative.");
        _ = checked(x + document.Width);
        _ = checked(y + document.Height);
        var tiles = SceneTileArea.Create(document.Width, document.Height, document.Tiles, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++) destination[i] = ToSceneTile(source[i]);
        });
        var chests = document.Chests.Select(chest => new SceneVirtualChestDefinition(VirtualChestId.New(), chest.X, chest.Y,
            chest.Name, 1, chest.Items.Select((item, slot) => new ChestItemSnapshot(slot, item.ItemType, item.Stack, item.Prefix)).ToArray())).ToArray();
        var signs = document.Signs.Select(sign => new SceneVirtualSignDefinition(VirtualSignId.New(), sign.X, sign.Y, sign.Text, 1)).ToArray();
        var frames = document.TileEntities.OfType<SchematicItemFrameTileEntity>().Select(frame =>
            new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(), frame.X, frame.Y, frame.Item.ItemType, frame.Item.Stack, frame.Item.Prefix, 1)).ToArray();
        return new SceneOverlayDefinition(default, name.Trim(), new(x, y, document.Width, document.Height), layer, observers, tiles)
        {
            VirtualChests = Array.AsReadOnly(chests),
            VirtualSigns = Array.AsReadOnly(signs),
            VirtualItemFrames = Array.AsReadOnly(frames)
        };
    }

    private static SceneTile ToSceneTile(SchematicTile tile)
    {
        // The two formats deliberately have different flag positions after WireYellow.
        if (tile.Shape > 5) throw new SchematicFormatException("Unsupported schematic tile shape.");
        SceneTileFlags flags = (SceneTileFlags)((ushort)tile.Flags & 31);
        if (tile.Shape == 1) flags |= SceneTileFlags.HalfBlock;
        if (tile.Flags.HasFlag(SchematicTileFlags.Actuator)) flags |= SceneTileFlags.Actuator;
        if (tile.Flags.HasFlag(SchematicTileFlags.Inactive)) flags |= SceneTileFlags.Inactive;
        if (tile.Flags.HasFlag(SchematicTileFlags.FullbrightBlock)) flags |= SceneTileFlags.FullbrightBlock;
        if (tile.Flags.HasFlag(SchematicTileFlags.FullbrightWall)) flags |= SceneTileFlags.FullbrightWall;
        if (tile.Flags.HasFlag(SchematicTileFlags.InvisibleBlock)) flags |= SceneTileFlags.InvisibleBlock;
        if (tile.Flags.HasFlag(SchematicTileFlags.InvisibleWall)) flags |= SceneTileFlags.InvisibleWall;
        return new(tile.Type, tile.Wall, tile.FrameX, tile.FrameY, tile.LiquidAmount, (byte)tile.LiquidKind,
            tile.TileColor, tile.WallColor, tile.Shape >= 2 ? (byte)(tile.Shape - 1) : (byte)0, flags);
    }
}
