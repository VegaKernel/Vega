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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (document.Chests.Any(chest => chest.Capacity != SceneVirtualChestDefinition.SlotCount))
            throw new SchematicFormatException("Expanded schematic chests require native placement; virtual preview supports forty slots only.");
        var (tiles, bounds) = CreateMaterialization(document, x, y);
        var chests = document.Chests.Select(chest => new SceneVirtualChestDefinition(VirtualChestId.New(), chest.X, chest.Y,
            chest.Name, 1, chest.Items.Select((item, slot) => new ChestItemSnapshot(slot, item.ItemType, item.Stack, item.Prefix)).ToArray())).ToArray();
        var signs = document.Signs.Select(sign => new SceneVirtualSignDefinition(VirtualSignId.New(), sign.X, sign.Y, sign.Text, 1)).ToArray();
        var frames = document.TileEntities.OfType<SchematicItemFrameTileEntity>().Select(frame =>
            new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(), frame.X, frame.Y, frame.Item.ItemType, frame.Item.Stack, frame.Item.Prefix, 1)).ToArray();
        var displays = document.TileEntities.Select(ToDisplay).Where(display => display is not null).Select(display => display!).ToArray();
        return new SceneOverlayDefinition(default, name.Trim(), bounds, layer, observers, tiles)
        {
            VirtualChests = Array.AsReadOnly(chests),
            VirtualSigns = Array.AsReadOnly(signs),
            VirtualItemFrames = Array.AsReadOnly(frames),
            VirtualDisplays = Array.AsReadOnly(displays)
        };
    }

    /// <summary>Prepare canonical placement tiles without constructing virtual chest or display projections.</summary>
    public static (SceneTileArea Tiles, SceneBounds Bounds) CreateMaterialization(SchematicDocument document, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        if (x < 0 || y < 0) throw new ArgumentOutOfRangeException(nameof(x), "Placement must be non-negative.");
        _ = checked(x + document.Width);
        _ = checked(y + document.Height);
        var tiles = SceneTileArea.Create(document.Width, document.Height, document.Tiles, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++) destination[i] = ToSceneTile(source[i]);
        });
        return (tiles, new(x, y, document.Width, document.Height));
    }

    private static SceneVirtualDisplayDefinition? ToDisplay(SchematicTileEntity entity)
    {
        static SceneDisplayItem Item(SchematicItemStack item) => new(item.ItemType, item.Stack, item.Prefix);
        static SceneDisplayItem[] Items(SchematicItemStack[] items) => items.Select(Item).ToArray();
        return entity switch
        {
            SchematicWeaponsRackTileEntity rack => new(VirtualDisplayId.New(), SceneVirtualDisplayKind.WeaponsRack, rack.X, rack.Y, [Item(rack.Item)], [], 1),
            SchematicDisplayDollTileEntity doll => new(VirtualDisplayId.New(), SceneVirtualDisplayKind.DisplayDoll, doll.X, doll.Y, Items(doll.Items), Items(doll.Dyes), 1),
            SchematicHatRackTileEntity hat => new(VirtualDisplayId.New(), SceneVirtualDisplayKind.HatRack, hat.X, hat.Y, Items(hat.Items), Items(hat.Dyes), 1),
            SchematicFoodPlatterTileEntity food => new(VirtualDisplayId.New(), SceneVirtualDisplayKind.FoodPlatter, food.X, food.Y, [Item(food.Item)], [], 1),
            _ => null
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
