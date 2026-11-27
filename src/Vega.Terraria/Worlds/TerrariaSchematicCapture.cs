using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;

namespace Vega.Terraria.Worlds;

/// <summary>Copies the represented native data on the world thread. No live objects escape the capture.</summary>
public sealed class TerrariaSchematicCapture(IGameThreadDispatcher dispatcher) : ISchematicCaptureSource
{
    public SchematicDocument Capture(in SchematicBounds bounds)
    {
        dispatcher.AssertGameThread();
        bounds.Validate();
        var region = new SceneBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (!TerrariaWorldTileService.ValidateBounds(region, out var error))
            throw new ArgumentException(error ?? "Invalid schematic bounds.");
        var tiles = new SchematicTile[checked(bounds.Width * bounds.Height)];
        int index = 0;
        for (int y = region.Y; y < region.Bottom; y++)
        for (int x = region.X; x < region.Right; x++)
            tiles[index++] = ToSchematicTile(TerrariaWorldTileService.Snapshot(global::Terraria.Main.tile[x, y]));

        bool Contains(int x, int y) => x >= region.X && y >= region.Y && x < region.Right && y < region.Bottom;
        float left = region.X * 16f, top = region.Y * 16f, right = region.Right * 16f, bottom = region.Bottom * 16f;
        bool ContainsPixel(float x, float y) => x >= left && y >= top && x < right && y < bottom;
        SchematicChest[] chests = global::Terraria.Main.chest.Where(chest => chest is not null && Contains(chest.x, chest.y))
            .Select(chest => CaptureChest(chest, region)).ToArray();
        SchematicSign[] signs = global::Terraria.Main.sign.Where(sign => sign is not null && Contains(sign.x, sign.y))
            .Select(sign => new SchematicSign(sign.x - region.X, sign.y - region.Y, sign.text ?? "")).ToArray();
        SchematicNpc[] npcs = global::Terraria.Main.npc.Take(global::Terraria.Main.maxNPCs)
            .Where(npc => npc is { active: true } && npc.type > 0 && ContainsPixel(npc.position.X, npc.position.Y))
            .Select(npc =>
            {
                bool homeless = npc.homeless || !Contains(npc.homeTileX, npc.homeTileY);
                return new SchematicNpc(npc.type, npc.position.X - left, npc.position.Y - top,
                    npc.direction < 0 ? (sbyte)-1 : (sbyte)1, npc.spriteDirection < 0 ? (sbyte)-1 : (sbyte)1,
                    npc.GivenName, homeless, homeless ? 0 : npc.homeTileX - region.X, homeless ? 0 : npc.homeTileY - region.Y,
                    npc.life > 0 ? npc.life : null);
            }).ToArray();
        SchematicWorldItem[] items = global::Terraria.Main.item.Take(global::Terraria.Main.maxItems)
            .Where(item => item is { active: true } && item.type > 0 && item.stack > 0 && ContainsPixel(item.position.X, item.position.Y))
            .Select(item => new SchematicWorldItem(new(item.type, item.stack, item.prefix), item.position.X - left, item.position.Y - top)).ToArray();
        SchematicTileEntity[] entities = TileEntity.ByPosition.Values.Where(entity => Contains(entity.Position.X, entity.Position.Y))
            .Select(entity => CaptureEntity(entity, region)).ToArray();
        return new SchematicDocument
        {
            ContentVersion = global::Terraria.Main.curRelease, Width = region.Width, Height = region.Height,
            Tiles = tiles, Chests = chests, Signs = signs, Npcs = npcs, WorldItems = items, TileEntities = entities
        };
    }

    private static SchematicChest CaptureChest(global::Terraria.Chest chest, SceneBounds bounds)
    {
        if (chest.maxItems is < SchematicLimits.DefaultChestCapacity or > SchematicLimits.MaxChestItems ||
            chest.maxItems > global::Terraria.Chest.AbsoluteMaxItemsWeCanEverReachInAChestForNow || chest.item.Length != chest.maxItems)
            throw new SchematicFormatException("Unsupported or inconsistent native chest capacity.");
        return new(chest.x - bounds.X, chest.y - bounds.Y, chest.name ?? "", chest.item.Select(Item).ToArray()) { Capacity = chest.maxItems };
    }

    private static SchematicTileEntity CaptureEntity(TileEntity entity, SceneBounds bounds)
    {
        int x = entity.Position.X - bounds.X, y = entity.Position.Y - bounds.Y;
        return entity switch
        {
            TETrainingDummy => new SchematicTrainingDummyTileEntity(x, y),
            TEItemFrame frame => new SchematicItemFrameTileEntity(x, y, Item(frame.item)),
            TELogicSensor sensor => new SchematicLogicSensorTileEntity(x, y, (byte)sensor.logicCheck, sensor.On),
            TEDisplayDoll doll => new SchematicDisplayDollTileEntity(x, y, Items(doll._equip), Items(doll._dyes)),
            TEWeaponsRack rack => new SchematicWeaponsRackTileEntity(x, y, Item(rack.item)),
            TEHatRack rack => new SchematicHatRackTileEntity(x, y, Items(rack._items), Items(rack._dyes)),
            TEFoodPlatter platter => new SchematicFoodPlatterTileEntity(x, y, Item(platter.item)),
            TETeleportationPylon => new SchematicTeleportationPylonTileEntity(x, y),
            _ => throw new SchematicFormatException($"Tile entity {entity.GetType().Name} is not represented by .trschem v1.")
        };
    }

    private static SchematicItemStack[] Items(global::Terraria.Item[] items) => items.Select(Item).ToArray();
    private static SchematicItemStack Item(global::Terraria.Item? item) => item is { type: > 0, stack: > 0 }
        ? new(item.type, item.stack, item.prefix) : default;

    internal static SchematicTile ToSchematicTile(SceneTile tile)
    {
        bool half = tile.Flags.HasFlag(SceneTileFlags.HalfBlock);
        if (tile.Slope > 4 || half && tile.Slope != 0)
            throw new SchematicFormatException("This tile shape cannot be represented losslessly by .trschem v1.");
        SchematicTileFlags flags = (SchematicTileFlags)((ushort)tile.Flags & 31);
        if (tile.Flags.HasFlag(SceneTileFlags.Actuator)) flags |= SchematicTileFlags.Actuator;
        if (tile.Flags.HasFlag(SceneTileFlags.Inactive)) flags |= SchematicTileFlags.Inactive;
        if (tile.Flags.HasFlag(SceneTileFlags.FullbrightBlock)) flags |= SchematicTileFlags.FullbrightBlock;
        if (tile.Flags.HasFlag(SceneTileFlags.FullbrightWall)) flags |= SchematicTileFlags.FullbrightWall;
        if (tile.Flags.HasFlag(SceneTileFlags.InvisibleBlock)) flags |= SchematicTileFlags.InvisibleBlock;
        if (tile.Flags.HasFlag(SceneTileFlags.InvisibleWall)) flags |= SchematicTileFlags.InvisibleWall;
        return new(tile.TileType, tile.WallType, tile.FrameX, tile.FrameY, flags, tile.LiquidAmount, tile.TileColor,
            tile.WallColor, half ? (byte)1 : tile.Slope == 0 ? (byte)0 : (byte)(tile.Slope + 1), (SchematicLiquidKind)tile.LiquidKind);
    }
}
