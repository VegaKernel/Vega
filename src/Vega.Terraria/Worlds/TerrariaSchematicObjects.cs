using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Item = global::Terraria.Item;
using Chest = global::Terraria.Chest;
using Sign = global::Terraria.Sign;
using Main = global::Terraria.Main;

namespace Vega.Terraria.Worlds;

/// <summary>Prepare native object registrations before replacing tiles in an idle sandbox.</summary>
internal sealed class TerrariaSchematicObjects(SceneBounds bounds)
{
    private readonly List<(SchematicChest Record, Item[] Items, int Slot)> chests = [];
    private readonly List<SchematicSign> signs = [];
    private readonly List<(SchematicTileEntity Record, byte EntityType, Item[] Items, Item[] Dyes)> entities = [];
    private readonly List<Action> rollback = [];

    public static TerrariaSchematicObjects Prepare(SchematicDocument document, SceneTileArea tiles, SceneBounds bounds)
    {
        if (document.WorldItems.Length != 0)
            throw new SchematicFormatException("Native dropped world item placement is not supported.");
        var result = new TerrariaSchematicObjects(bounds);
        var covered = new bool[checked(bounds.Width * bounds.Height)];

        // Only complete standard 18-pixel framed footprints are accepted, never an anchor
        // whose registration would overlap another object or leave a partial object outside the region.
        void Cover(int x, int y, int width, int height, ushort type)
        {
            if (x < 0 || y < 0 || x > bounds.Width - width || y > bounds.Height - height)
                throw new SchematicFormatException("Registered object footprint exceeds its schematic region.");
            var anchor = tiles[x, y];
            if (anchor.FrameX < 0 || anchor.FrameX % (18 * width) != 0 || anchor.FrameY != 0)
                throw new SchematicFormatException("Registered object anchor is not a supported native frame.");
            for (int dy = 0; dy < height; dy++)
            for (int dx = 0; dx < width; dx++)
            {
                int index = (y + dy) * bounds.Width + x + dx;
                var tile = tiles[x + dx, y + dy];
                if (covered[index] || !tile.Flags.HasFlag(SceneTileFlags.Active) || tile.TileType != type ||
                    tile.FrameX != anchor.FrameX + 18 * dx || tile.FrameY != 18 * dy)
                    throw new SchematicFormatException("Registered object has overlapping, missing or mismatched native tiles.");
                covered[index] = true;
            }
        }

        var freeChests = Enumerable.Range(0, Main.chest.Length).Where(i => Main.chest[i] is null)
            .Take(document.Chests.Length).ToArray();
        if (freeChests.Length != document.Chests.Length || document.Signs.Length > Main.sign.Count(sign => sign is null))
            throw new SchematicFormatException("Insufficient native chest or sign slots.");
        for (int i = 0; i < document.Chests.Length; i++)
        {
            var record = document.Chests[i];
            if ((uint)record.X >= (uint)bounds.Width || (uint)record.Y >= (uint)bounds.Height)
                throw new SchematicFormatException("Chest anchor lies outside its schematic region.");
            ushort type = tiles[record.X, record.Y].TileType;
            if (type is not (TileID.Containers or TileID.Containers2 or TileID.Dressers) ||
                record.Name.Length > Chest.MaxNameLength || record.Items.Length > Chest.DefaultMaxItems ||
                Chest.FindChest(bounds.X + record.X, bounds.Y + record.Y) >= 0)
                throw new SchematicFormatException("Unsupported native chest type, name, capacity or existing registration.");
            Cover(record.X, record.Y, type == TileID.Dressers ? 3 : 2, 2, type);
            result.chests.Add((record, Items(record.Items), freeChests[i]));
        }
        foreach (var record in document.Signs)
        {
            if ((uint)record.X >= (uint)bounds.Width || (uint)record.Y >= (uint)bounds.Height)
                throw new SchematicFormatException("Sign anchor lies outside its schematic region.");
            ushort type = tiles[record.X, record.Y].TileType;
            if (!Main.tileSign[type]) throw new SchematicFormatException("Sign record requires a native sign tile.");
            Cover(record.X, record.Y, 2, 2, type);
            result.signs.Add(record);
        }
        foreach (var record in document.TileEntities)
        {
            var (type, width, height, entityType, items, dyes) = record switch
            {
                SchematicTrainingDummyTileEntity => (TileID.TargetDummy, 2, 3, TETrainingDummy.EntityTypeID, Array.Empty<Item>(), Array.Empty<Item>()),
                SchematicItemFrameTileEntity frame => (TileID.ItemFrame, 2, 2, TEItemFrame.EntityTypeID, Items([frame.Item]), Array.Empty<Item>()),
                SchematicDisplayDollTileEntity doll => (TileID.DisplayDoll, 2, 3, TEDisplayDoll.EntityTypeID, Items(doll.Items), Items(doll.Dyes)),
                SchematicWeaponsRackTileEntity rack => (TileID.WeaponsRack2, 3, 3, TEWeaponsRack.EntityTypeID, Items([rack.Item]), Array.Empty<Item>()),
                SchematicHatRackTileEntity rack => (TileID.HatRack, 3, 4, TEHatRack.EntityTypeID, Items(rack.Items), Items(rack.Dyes)),
                SchematicFoodPlatterTileEntity platter => (TileID.FoodPlatter, 1, 1, TEFoodPlatter.EntityTypeID, Items([platter.Item]), Array.Empty<Item>()),
                _ => throw new SchematicFormatException($"Native placement of {record.Kind} is not supported.")
            };
            Cover(record.X, record.Y, width, height, type);
            result.entities.Add((record, entityType, items, dyes));
        }
        for (int i = 0; i < covered.Length; i++)
            if (!covered[i] && NeedsRegisteredObject(tiles.Tiles[i]))
                throw new SchematicFormatException("Schematic has a native object tile without its complete registration record.");
        return result;
    }

    public void Apply()
    {
        foreach (var (record, items, slot) in chests)
        {
            int x = bounds.X + record.X, y = bounds.Y + record.Y;
            if (Main.chest[slot] is not null || Chest.FindChest(x, y) >= 0)
                throw new InvalidOperationException("Native chest slot changed during placement.");
            var chest = Chest.CreateWorldChest(slot, x, y);
            rollback.Add(() => { if (ReferenceEquals(Main.chest[slot], chest)) Chest.RemoveChest(slot); });
            if (!ReferenceEquals(Main.chest[slot], chest) || Chest.FindChest(x, y) != slot)
                throw new InvalidOperationException("Native chest registration failed.");
            chest.name = record.Name;
            CopyItems(items, chest.item);
        }
        foreach (var record in signs)
        {
            int x = bounds.X + record.X, y = bounds.Y + record.Y;
            int slot = Sign.ReadSign(x, y, true);
            if ((uint)slot >= Main.sign.Length || Main.sign[slot] is not { } sign || sign.x != x || sign.y != y)
                throw new InvalidOperationException("Native sign registration failed.");
            rollback.Add(() => { if (ReferenceEquals(Main.sign[slot], sign)) Main.sign[slot] = null; });
            sign.text = record.Text;
        }
        foreach (var (record, entityType, items, dyes) in entities)
        {
            int x = bounds.X + record.X, y = bounds.Y + record.Y;
            var position = new Point16(x, y);
            if (TileEntity.ByPosition.ContainsKey(position) || !TileEntity.manager.CheckValidTile(entityType, x, y))
                throw new InvalidOperationException($"Native {record.Kind} tile entity cannot register on the applied footprint.");
            // Some native Place implementations run OnPlaced after adding to the dictionaries.
            // Track the exact new object even if that callback throws before returning its ID.
            int id;
            try { id = TileEntity.Place(x, y, entityType); }
            finally
            {
                if (TileEntity.ByPosition.TryGetValue(position, out var placed) && placed.type == entityType)
                    rollback.Add(() =>
                    {
                        if (TileEntity.ByPosition.TryGetValue(position, out var current) && ReferenceEquals(current, placed))
                            TileEntity.Kill(x, y, entityType);
                    });
            }
            if (!TileEntity.ByID.TryGetValue(id, out var entity) || entity.Position != position)
                throw new InvalidOperationException("Native tile entity registration failed.");
            switch (record, entity)
            {
                case (SchematicTrainingDummyTileEntity, TETrainingDummy): break;
                case (SchematicItemFrameTileEntity, TEItemFrame frame): frame.item = items[0]; break;
                case (SchematicDisplayDollTileEntity, TEDisplayDoll doll): CopyItems(items, doll._equip); CopyItems(dyes, doll._dyes); break;
                case (SchematicWeaponsRackTileEntity, TEWeaponsRack rack): rack.item = items[0]; break;
                case (SchematicHatRackTileEntity, TEHatRack rack): CopyItems(items, rack._items); CopyItems(dyes, rack._dyes); break;
                case (SchematicFoodPlatterTileEntity, TEFoodPlatter platter): platter.item = items[0]; break;
                default: throw new InvalidOperationException("Native tile entity type does not match the schematic.");
            }
        }
    }

    public Exception? Rollback()
    {
        Exception? failure = null;
        for (int i = rollback.Count - 1; i >= 0; i--)
        {
            try { rollback[i](); }
            catch (Exception error) { failure ??= error; }
        }
        return failure;
    }

    private static Item[] Items(SchematicItemStack[] records)
    {
        var items = new Item[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            var record = records[i];
            var item = items[i] = new Item();
            if (record.IsEmpty) continue;
            if (record.ItemType <= 0 || record.ItemType >= ItemID.Count || record.Prefix >= PrefixID.Count)
                throw new SchematicFormatException("Unsupported native schematic item type or prefix.");
            item.SetDefaults(record.ItemType);
            if (record.Prefix != 0) item.Prefix(record.Prefix);
            if (item.type != record.ItemType || item.prefix != record.Prefix || record.Stack <= 0 || record.Stack > item.maxStack)
                throw new SchematicFormatException("Schematic item cannot be represented by native defaults.");
            item.stack = record.Stack;
        }
        return items;
    }

    private static void CopyItems(Item[] items, Item[] destination)
    {
        if (items.Length > destination.Length) throw new SchematicFormatException("Schematic inventory exceeds native slot capacity.");
        Array.Copy(items, destination, items.Length);
    }

    private static bool NeedsRegisteredObject(SceneTile tile)
    {
        if (!tile.Flags.HasFlag(SceneTileFlags.Active)) return false;
        int type = tile.TileType;
        return Main.tileContainer[type] || Main.tileSign[type] ||
            type is TileID.Containers or TileID.Containers2 or TileID.Dressers or TileID.Signs or TileID.Tombstones or
                TileID.AnnouncementBox or TileID.ArrowSign or TileID.PaintedArrowSign or TileID.TatteredWoodSign or
                TileID.TargetDummy or TileID.ItemFrame or TileID.LogicSensor or TileID.DisplayDoll or TileID.WeaponsRack2 or
                TileID.HatRack or TileID.FoodPlatter or TileID.TeleportationPylon or TileID.DeadCellsDisplayJar or
                TileID.KiteAnchor or TileID.CritterAnchor;
    }
}
