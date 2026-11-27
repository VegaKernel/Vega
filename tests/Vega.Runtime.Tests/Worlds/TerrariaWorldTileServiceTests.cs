using NUnit.Framework;
using System.Reflection;
using Terraria.DataStructures;
using Terraria.GameContent.Tile_Entities;
using Terraria.ID;
using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;
using ITile = global::Terraria.ITile;
using Tile = global::Terraria.Tile;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class TerrariaWorldTileServiceTests
{
    [Test]
    [NonParallelizable]
    public void SandboxRestoresNativeObjectRegistrationsAndRoundTripsTypedEntities()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY, oldMode = global::Terraria.Main.netMode;
        bool oldMenu = global::Terraria.Main.gameMenu, oldDedicated = global::Terraria.Main.dedServ;
        var oldChest = global::Terraria.Main.chest;
        var oldChestCoords = global::Terraria.Chest._chestsByCoords;
        var oldSigns = global::Terraria.Main.sign;
        var oldNpc = global::Terraria.Main.npc[0];
        bool oldSignType = global::Terraria.Main.tileSign[TileID.Signs];
        var oldManager = TileEntity.manager;
        var oldById = TileEntity.ByID;
        var oldByPosition = TileEntity.ByPosition;
        var oldUpdates = TileEntity.UpdateEntities;
        int oldNextId = TileEntity.TileEntitiesNextID;
        var updateStart = typeof(TileEntity).GetField("_UpdateStart", BindingFlags.Static | BindingFlags.NonPublic)!;
        var updateEnd = typeof(TileEntity).GetField("_UpdateEnd", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? oldStart = updateStart.GetValue(null), oldEnd = updateEnd.GetValue(null);
        byte oldDummy = TETrainingDummy.EntityTypeID, oldFrame = TEItemFrame.EntityTypeID, oldDoll = TEDisplayDoll.EntityTypeID,
            oldWeapon = TEWeaponsRack.EntityTypeID, oldHat = TEHatRack.EntityTypeID, oldPlatter = TEFoodPlatter.EntityTypeID;
        try
        {
            global::Terraria.Main.gameMenu = false;
            global::Terraria.Main.netMode = 0;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.maxTilesX = 32;
            global::Terraria.Main.maxTilesY = 24;
            global::Terraria.Main.tile = CreateGrid(32, 24);
            global::Terraria.Main.chest = new global::Terraria.Chest[oldChest.Length];
            global::Terraria.Chest._chestsByCoords = [];
            global::Terraria.Main.sign = new global::Terraria.Sign[oldSigns.Length];
            global::Terraria.Main.tileSign[TileID.Signs] = true;
            global::Terraria.Main.npc[0] = new global::Terraria.NPC();
            TileEntity.manager = new TileEntitiesManager();
            TileEntity.ByID = [];
            TileEntity.ByPosition = [];
            TileEntity.UpdateEntities = [];
            TileEntity.TileEntitiesNextID = 0;
            TileEntity.manager.Register(new TETrainingDummy());
            TileEntity.manager.Register(new TEItemFrame());
            TileEntity.manager.Register(new TEDisplayDoll());
            TileEntity.manager.Register(new TEWeaponsRack());
            TileEntity.manager.Register(new TEHatRack());
            TileEntity.manager.Register(new TEFoodPlatter());

            var bounds = new SceneBounds(4, 4, 20, 12);
            var source = new SceneTile[bounds.Width * bounds.Height];
            void AddObject(int x, int y, int width, int height, ushort type)
            {
                for (int dy = 0; dy < height; dy++)
                for (int dx = 0; dx < width; dx++)
                    source[(y + dy) * bounds.Width + x + dx] = new SceneTile(type, FrameX: (short)(dx * 18), FrameY: (short)(dy * 18), Flags: SceneTileFlags.Active);
            }
            AddObject(0, 0, 2, 2, TileID.Containers);
            AddObject(3, 0, 2, 2, TileID.Signs);
            AddObject(6, 0, 2, 2, TileID.ItemFrame);
            AddObject(9, 0, 3, 3, TileID.WeaponsRack2);
            AddObject(13, 0, 3, 4, TileID.HatRack);
            AddObject(17, 0, 1, 1, TileID.FoodPlatter);
            AddObject(0, 5, 2, 3, TileID.TargetDummy);
            AddObject(3, 5, 2, 3, TileID.DisplayDoll);
            var area = new SceneTileArea(bounds.Width, bounds.Height, source);
            var items = new[] { new SchematicItemStack(ItemID.Torch, 4) };
            var sword = new SchematicItemStack(ItemID.CopperShortsword, 1, PrefixID.Large);
            SchematicDocument Document(bool failNpc = false, bool omitFrame = false) => new()
            {
                ContentVersion = 279, Width = bounds.Width, Height = bounds.Height,
                Tiles = source.Select(TerrariaSchematicCapture.ToSchematicTile).ToArray(),
                Chests = [new(0, 0, "Loot", items)], Signs = [new(3, 0, "Arena sign")],
                TileEntities = new SchematicTileEntity[]
                {
                    new SchematicItemFrameTileEntity(6, 0, sword), new SchematicWeaponsRackTileEntity(9, 0, sword),
                    new SchematicHatRackTileEntity(13, 0, [new(ItemID.CopperHelmet, 1)], []),
                    new SchematicFoodPlatterTileEntity(17, 0, new(ItemID.Apple, 1)),
                    new SchematicTrainingDummyTileEntity(0, 5),
                    new SchematicDisplayDollTileEntity(3, 5, [new(ItemID.CopperHelmet, 1)], [])
                }.Where(entity => !omitFrame || entity is not SchematicItemFrameTileEntity).ToArray(),
                Npcs = failNpc ? [new(22, 160, 120)] : []
            };
            var placement = new TerrariaSchematicPlacement(new InlineGameThread(), new WorldId("arena"))
            { SpawnNpc = (_, _, _) => global::Terraria.Main.maxNPCs };
            var original = TerrariaWorldTileService.CaptureArea(global::Terraria.Main.tile, bounds).Area;
            Assert.That(placement.Place(Document(omitFrame: true), area, bounds).Status, Is.EqualTo(WorldSchematicPlacementStatus.Invalid));
            Assert.That(TerrariaWorldTileService.CaptureArea(global::Terraria.Main.tile, bounds).Area.Tiles, Is.EqualTo(original.Tiles));
            var failure = placement.Place(Document(failNpc: true), area, bounds);
            Assert.That(failure.Status, Is.EqualTo(WorldSchematicPlacementStatus.Failed), failure.Error);
            Assert.That(failure.Error, Does.Contain("NPC allocation"));
            Assert.That(global::Terraria.Chest.FindChest(bounds.X, bounds.Y), Is.EqualTo(-1));
            Assert.That(global::Terraria.Main.sign.All(sign => sign is null), Is.True);
            Assert.That(TileEntity.ByPosition, Is.Empty);
            Assert.That(TileEntity.ByID, Is.Empty);
            Assert.That(TileEntity.UpdateEntities, Is.Empty);
            Assert.That(TerrariaWorldTileService.CaptureArea(global::Terraria.Main.tile, bounds).Area.Tiles, Is.EqualTo(original.Tiles));

            var placed = placement.Place(Document(), area, bounds);
            Assert.That(placed.Status, Is.EqualTo(WorldSchematicPlacementStatus.Applied), placed.Error);
            Assert.That(global::Terraria.Chest.FindChest(bounds.X, bounds.Y), Is.GreaterThanOrEqualTo(0));
            Assert.That(global::Terraria.Sign.ReadSign(bounds.X + 3, bounds.Y, false), Is.GreaterThanOrEqualTo(0));
            Assert.That(TileEntity.ByID.Count, Is.EqualTo(6));
            var captured = new TerrariaSchematicCapture(new InlineGameThread()).Capture(new SchematicBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            captured = SchematicBinary.Deserialize(SchematicBinary.Serialize(captured));
            Assert.That(captured.Chests.Single().Name, Is.EqualTo("Loot"));
            Assert.That(captured.Chests.Single().Items[0], Is.EqualTo(items[0]));
            Assert.That(captured.Signs.Single().Text, Is.EqualTo("Arena sign"));
            Assert.That(captured.TileEntities.OfType<SchematicItemFrameTileEntity>().Single().Item, Is.EqualTo(sword));
            Assert.That(captured.TileEntities.OfType<SchematicWeaponsRackTileEntity>().Single().Item, Is.EqualTo(sword));
            Assert.That(captured.TileEntities.OfType<SchematicHatRackTileEntity>().Single().Items[0].ItemType, Is.EqualTo(ItemID.CopperHelmet));
            Assert.That(captured.TileEntities.OfType<SchematicFoodPlatterTileEntity>().Single().Item.ItemType, Is.EqualTo(ItemID.Apple));
            Assert.That(captured.TileEntities.OfType<SchematicDisplayDollTileEntity>().Single().Items[0].ItemType, Is.EqualTo(ItemID.CopperHelmet));
            Assert.That(captured.TileEntities.OfType<SchematicTrainingDummyTileEntity>().Count(), Is.EqualTo(1));
        }
        finally
        {
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.gameMenu = oldMenu;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.chest = oldChest;
            global::Terraria.Chest._chestsByCoords = oldChestCoords;
            global::Terraria.Main.sign = oldSigns;
            global::Terraria.Main.npc[0] = oldNpc;
            global::Terraria.Main.tileSign[TileID.Signs] = oldSignType;
            TileEntity.manager = oldManager;
            TileEntity.ByID = oldById;
            TileEntity.ByPosition = oldByPosition;
            TileEntity.UpdateEntities = oldUpdates;
            TileEntity.TileEntitiesNextID = oldNextId;
            updateStart.SetValue(null, oldStart);
            updateEnd.SetValue(null, oldEnd);
            TETrainingDummy.EntityTypeID = oldDummy;
            TEItemFrame.EntityTypeID = oldFrame;
            TEDisplayDoll.EntityTypeID = oldDoll;
            TEWeaponsRack.EntityTypeID = oldWeapon;
            TEHatRack.EntityTypeID = oldHat;
            TEFoodPlatter.EntityTypeID = oldPlatter;
        }
    }

    [Test]
    [NonParallelizable]
    public void SandboxPlacementRestoresNpcFieldsAndRollsBackOwnedTilesOnAllocationFailure()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        bool oldMenu = global::Terraria.Main.gameMenu;
        var previousNpc0 = global::Terraria.Main.npc[0];
        var previousNpc1 = global::Terraria.Main.npc[1];
        try
        {
            global::Terraria.Main.gameMenu = false;
            global::Terraria.Main.maxTilesX = global::Terraria.Main.maxTilesY = 8;
            var grid = CreateGrid(8, 8);
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.npc[0] = new global::Terraria.NPC();
            global::Terraria.Main.npc[1] = new global::Terraria.NPC();
            var bounds = new SceneBounds(2, 2, 2, 2);
            var original = TerrariaWorldTileService.CaptureArea(grid, bounds).Area;
            var replacement = SceneTileArea.Fill(2, 2, new SceneTile(1, Flags: SceneTileFlags.Active));
            var npc = new SchematicNpc(22, 8, 12, -1, 1, "Guide", false, 1, 1, 75);
            SchematicDocument Document(params SchematicNpc[] npcs) => new() { ContentVersion = 279, Width = 2, Height = 2, Tiles = new SchematicTile[4], Npcs = npcs };
            int Spawn(int x, int y, int type)
            {
                global::Terraria.Main.npc[0] = new global::Terraria.NPC { active = true, type = type, generation = 5, lifeMax = 100 };
                return 0;
            }
            var primary = new TerrariaSchematicPlacement(new InlineGameThread(), WorldId.Primary) { SpawnNpc = Spawn };
            Assert.That(primary.Place(Document(npc), replacement, bounds).Status, Is.EqualTo(WorldSchematicPlacementStatus.Unavailable));
            int attempts = 0;
            var failure = new TerrariaSchematicPlacement(new InlineGameThread(), new WorldId("arena"))
            { SpawnNpc = (x, y, type) => attempts++ == 0 ? Spawn(x, y, type) : global::Terraria.Main.maxNPCs };
            Assert.That(failure.Place(Document(npc, npc), replacement, bounds).Status, Is.EqualTo(WorldSchematicPlacementStatus.Failed));
            Assert.That(global::Terraria.Main.npc[0].active, Is.False);
            Assert.That(TerrariaWorldTileService.CaptureArea(grid, bounds).Area.Tiles, Is.EqualTo(original.Tiles));
            var target = new TerrariaSchematicPlacement(new InlineGameThread(), new WorldId("arena")) { SpawnNpc = Spawn };
            Assert.That(target.Place(Document(npc), SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active)), bounds).Status,
                Is.EqualTo(WorldSchematicPlacementStatus.Invalid));
            Assert.That(target.Place(Document(npc), replacement, bounds), Is.EqualTo(new WorldSchematicPlacementResult(WorldSchematicPlacementStatus.Applied, 1)));
            var placed = global::Terraria.Main.npc[0];
            Assert.That(placed.position.X, Is.EqualTo(40));
            Assert.That(placed.position.Y, Is.EqualTo(44));
            Assert.That(placed.GivenName, Is.EqualTo("Guide"));
            Assert.That(placed.homeTileX, Is.EqualTo(3));
            Assert.That(placed.homeTileY, Is.EqualTo(3));
            Assert.That(placed.direction, Is.EqualTo(-1));
            Assert.That(placed.life, Is.EqualTo(75));
            Assert.That(TerrariaWorldTileService.CaptureArea(grid, bounds).Area.Tiles, Is.EqualTo(replacement.Tiles));
        }
        finally
        {
            global::Terraria.Main.npc[0] = previousNpc0;
            global::Terraria.Main.npc[1] = previousNpc1;
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.gameMenu = oldMenu;
        }
    }

    [Test]
    [NonParallelizable]
    public void SchematicCaptureDetachesNativeRegionAndNpcMetadata()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        var oldNpc = global::Terraria.Main.npc[0];
        try
        {
            var grid = CreateGrid(5, 4);
            global::Terraria.Main.tile = grid;
            global::Terraria.Main.maxTilesX = 5;
            global::Terraria.Main.maxTilesY = 4;
            var npc = new global::Terraria.NPC
            {
                active = true, type = 22, position = new(32, 32), direction = -1, spriteDirection = 1,
                GivenName = "Test Guide", homeless = false, homeTileX = 2, homeTileY = 2, life = 100
            };
            global::Terraria.Main.npc[0] = npc;
            var capture = new TerrariaSchematicCapture(new InlineGameThread());
            var document = capture.Capture(new SchematicBounds(1, 1, 3, 3));
            var captured = document.Npcs.Single(n => n.Name == "Test Guide");
            Assert.That(captured, Is.EqualTo(new SchematicNpc(22, 16, 16, -1, 1, "Test Guide", false, 1, 1, 100)));
            Assert.That(document.Tiles[0].Type, Is.EqualTo(grid[1, 1].type));
            npc.GivenName = "Changed";
            grid[1, 1].type = 999;
            var roundtrip = SchematicBinary.Deserialize(SchematicBinary.Serialize(document));
            Assert.That(roundtrip.Npcs.Single(n => n.Name == "Test Guide"), Is.EqualTo(captured));
            Assert.That(roundtrip.Tiles[0].Type, Is.Not.EqualTo(999));
        }
        finally
        {
            global::Terraria.Main.npc[0] = oldNpc;
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
        }
    }

    [Test]
    public void OwnedAreaFactoryAndPublicConstructorDoNotExposeMutableInput()
    {
        SceneTile[] source = [new(1), new(2), new(3), new(4)];
        SceneTileArea created = SceneTileArea.Create(2, 2, source,
            static (destination, input) => input.AsSpan().CopyTo(destination));
        var copied = new SceneTileArea(2, 2, source);
        source[0] = new SceneTile(99);
        Assert.That(created[0, 0].TileType, Is.EqualTo(1));
        Assert.That(created[1, 1].TileType, Is.EqualTo(4));
        Assert.That(copied[0, 0].TileType, Is.EqualTo(1));
        Assert.That(() => ((IList<SceneTile>)created.Tiles)[0] = new SceneTile(99), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void CombinedCaptureRetainsLegacyRowMajorFingerprintAndNativeFields()
    {
        var native = CreateGrid(5, 4);
        var bounds = new SceneBounds(1, 1, 3, 2);
        var (area, fingerprint) = TerrariaWorldTileService.CaptureArea(native, bounds);
        var legacy = new HashCode();
        legacy.Add(bounds.Width);
        legacy.Add(bounds.Height);
        foreach (SceneTile tile in area.Tiles) legacy.Add(tile);
        Assert.That(fingerprint, Is.EqualTo((long)legacy.ToHashCode()));

        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
            {
                ITile original = native[bounds.X + x, bounds.Y + y];
                SceneTile captured = area[x, y];
                Assert.That(captured.TileType, Is.EqualTo(original.type));
                Assert.That(captured.WallType, Is.EqualTo(original.wall));
                Assert.That(captured.FrameX, Is.EqualTo(original.frameX));
                Assert.That(captured.FrameY, Is.EqualTo(original.frameY));
                Assert.That(captured.LiquidAmount, Is.EqualTo(original.liquid));
                Assert.That(captured.LiquidKind, Is.EqualTo(original.liquidType()));
                Assert.That(captured.TileColor, Is.EqualTo(original.color()));
                Assert.That(captured.WallColor, Is.EqualTo(original.wallColor()));
                Assert.That(captured.Slope, Is.EqualTo(original.slope()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.Active), Is.EqualTo(original.active()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.WireRed), Is.EqualTo(original.wire()));
                Assert.That(captured.Flags.HasFlag(SceneTileFlags.FullbrightBlock), Is.EqualTo(original.fullbrightBlock()));
            }
    }

    [Test]
    [NonParallelizable]
    public async Task CapturedAreaIsDetachedAndRevisionsStillGuardReplacement()
    {
        var oldGrid = global::Terraria.Main.tile;
        int oldWidth = global::Terraria.Main.maxTilesX, oldHeight = global::Terraria.Main.maxTilesY;
        int oldMode = global::Terraria.Main.netMode;
        var native = CreateGrid(4, 4);
        try
        {
            global::Terraria.Main.tile = native;
            global::Terraria.Main.maxTilesX = 4;
            global::Terraria.Main.maxTilesY = 4;
            global::Terraria.Main.netMode = 0;
            var tiles = new TerrariaWorldTileService(new InlineGameThread(), WorldId.Primary);
            var bounds = new SceneBounds(1, 1, 2, 2);
            var original = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            var unchanged = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            Assert.That(unchanged.Revision, Is.EqualTo(original.Revision));
            ushort firstType = original.Tiles[0, 0].TileType;
            native[1, 1].type = (ushort)(firstType + 1);
            var changed = (await tiles.CaptureAsync(WorldId.Primary, bounds))!;
            Assert.That(changed.Revision, Is.EqualTo(original.Revision + 1));
            Assert.That(original.Tiles[0, 0].TileType, Is.EqualTo(firstType));

            SceneTileArea replacement = SceneTileArea.Fill(2, 2, new SceneTile(1, Flags: SceneTileFlags.Active));
            var conflict = await tiles.ReplaceAsync(new WorldTileMutationRequest(WorldId.Primary, bounds,
                original.Revision, replacement, "test"));
            Assert.That(conflict.Status, Is.EqualTo(WorldTileMutationStatus.RevisionConflict));
            Assert.That(conflict.Before!.Revision, Is.EqualTo(changed.Revision));
            Assert.That(native[1, 1].type, Is.EqualTo(firstType + 1));

            var applied = await tiles.ReplaceAsync(new WorldTileMutationRequest(WorldId.Primary, bounds,
                changed.Revision, replacement, "test"));
            Assert.That(applied.Status, Is.EqualTo(WorldTileMutationStatus.Applied));
            Assert.That(applied.Before!.Revision, Is.EqualTo(changed.Revision));
            Assert.That(applied.After!.Revision, Is.EqualTo(changed.Revision + 1));
            Assert.That(applied.After.Tiles.Tiles, Is.EqualTo(replacement.Tiles));
            Assert.That(original.Tiles[0, 0].TileType, Is.EqualTo(firstType));
        }
        finally
        {
            global::Terraria.Main.tile = oldGrid;
            global::Terraria.Main.maxTilesX = oldWidth;
            global::Terraria.Main.maxTilesY = oldHeight;
            global::Terraria.Main.netMode = oldMode;
        }
    }

    private static global::TerrariaRuntime.DefaultCollection<ITile> CreateGrid(int width, int height)
    {
        var native = new global::TerrariaRuntime.DefaultCollection<ITile>(width, height);
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                var tile = new Tile { type = (ushort)(2 + x + y * width), wall = 1,
                    frameX = (short)(18 * x), frameY = (short)(18 * y), liquid = (byte)(x + y) };
                tile.active((x + y) % 2 == 0);
                tile.wire(x % 2 == 0);
                tile.fullbrightBlock(y % 2 == 0);
                tile.liquidType(y % 4);
                tile.color((byte)x);
                tile.wallColor((byte)y);
                tile.slope((byte)(x % 4));
                native[x, y] = tile;
            }
        return native;
    }

    private sealed class InlineGameThread : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => step();
    }
}
