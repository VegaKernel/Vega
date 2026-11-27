using Terraria.DataStructures;
using Terraria.ID;
using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>Native materialization into an unoccupied region of an isolated, idle sandbox.</summary>
public sealed class TerrariaSchematicPlacement(IGameThreadDispatcher dispatcher, WorldId worldId)
{
    internal Func<int, int, int, int> SpawnNpc { get; init; } = static (x, y, type) =>
        global::Terraria.NPC.NewNPC(new EntitySource_DebugCommand(), x, y, type);

    public WorldSchematicPlacementResult Place(SchematicDocument document, SceneTileArea tiles, SceneBounds bounds)
    {
        dispatcher.AssertGameThread();
        if (worldId == WorldId.Primary || global::Terraria.Main.gameMenu ||
            global::Terraria.Netplay.Clients.Any(static client => client is { IsActive: true }))
            return new(WorldSchematicPlacementStatus.Unavailable, Error: "Placement requires a ready isolated sandbox with no connected clients.");
        if (!TerrariaWorldTileService.ValidateBounds(bounds, out string? error) ||
            bounds.Width != document.Width || bounds.Height != document.Height || tiles.Width != bounds.Width || tiles.Height != bounds.Height ||
            tiles.Tiles.Any(static tile => !TerrariaWorldTileService.IsValid(tile)))
            return Invalid(error ?? "Schematic tile bounds or native tile fields are invalid.");

        bool Overlaps(int x, int y, int width, int height) => x < bounds.Right && y < bounds.Bottom &&
            x + width > bounds.X && y + height > bounds.Y;
        bool OverlapsPixel(float x, float y, int width, int height) => x < bounds.Right * 16f && y < bounds.Bottom * 16f &&
            x + width > bounds.X * 16f && y + height > bounds.Y * 16f;
        // Do not orphan native object registrations. Tile-entity footprints can extend beyond
        // their anchor; a four-tile halo conservatively excludes neighboring registered objects.
        if (global::Terraria.Main.chest.Any(chest => chest is not null && Overlaps(chest.x, chest.y, 3, 2)) ||
            global::Terraria.Main.sign.Any(sign => sign is not null && Overlaps(sign.x, sign.y, 2, 2)) ||
            TileEntity.ByPosition.Values.Any(entity => Overlaps(entity.Position.X, entity.Position.Y, 4, 4)) ||
            global::Terraria.Main.npc.Any(npc => npc is { active: true } && OverlapsPixel(npc.position.X, npc.position.Y, npc.width, npc.height)) ||
            global::Terraria.Main.item.Any(item => item is { active: true } && OverlapsPixel(item.position.X, item.position.Y, item.width, item.height)))
            return Invalid("Destination contains registered native objects or entities; choose an unoccupied area.");

        int slots = Math.Min(global::Terraria.Main.maxNPCs, global::Terraria.Main.npc.Length);
        bool[] occupied = global::Terraria.Main.npc.Take(slots).Select(static npc => npc is null || npc.active).ToArray();
        if (document.Npcs.Length > occupied.Count(static active => !active) ||
            document.Npcs.Any(npc => npc.NpcType <= 0 || npc.NpcType >= NPCID.Count))
            return Invalid("Insufficient native NPC slots or unsupported NPC type.");
        TerrariaSchematicObjects objects;
        try { objects = TerrariaSchematicObjects.Prepare(document, tiles, bounds); }
        catch (SchematicFormatException ex) { return Invalid(ex.Message); }
        // Keep the original tile array and track only this operation's exact new NPC generations.
        var (before, _) = TerrariaWorldTileService.CaptureArea(global::Terraria.Main.tile, bounds);
        var created = new List<(int Slot, int Generation)>(document.Npcs.Length);
        try
        {
            ApplyTiles(tiles, bounds);
            objects.Apply();
            PlaceNpcs(document, bounds, occupied, created);
            return new(WorldSchematicPlacementStatus.Applied, created.Count);
        }
        catch (Exception ex)
        {
            // This is a sandbox-only operation. Roll back owned slots and tiles; other native/plugin
            // side effects are not transactional. A failed placement is not a ready world template.
            foreach (var (slot, generation) in created)
                if (global::Terraria.Main.npc[slot] is { } npc && npc.generation == generation) npc.active = false;
            Exception? rollbackError = objects.Rollback();
            try { ApplyTiles(before, bounds); }
            catch (Exception tileRestoreError) { rollbackError ??= tileRestoreError; }
            return new(WorldSchematicPlacementStatus.Failed, Error: rollbackError is null ? ex.Message :
                $"{ex.Message} Rollback was incomplete: {rollbackError.Message}");
        }
    }

    private void PlaceNpcs(SchematicDocument document, SceneBounds bounds, bool[] occupied, List<(int Slot, int Generation)> created)
    {
        int slots = occupied.Length;
        foreach (var record in document.Npcs)
        {
            float x = bounds.X * 16f + record.X, y = bounds.Y * 16f + record.Y;
            int slot = SpawnNpc((int)x, (int)y, record.NpcType);
            if ((uint)slot >= (uint)slots || occupied[slot] || global::Terraria.Main.npc[slot] is not { active: true } npc)
                throw new InvalidOperationException("Native NPC allocation failed.");
            occupied[slot] = true;
            created.Add((slot, npc.generation));
            if (npc.type != record.NpcType || record.LifeOverride is int life && life > npc.lifeMax)
                throw new InvalidOperationException("Native NPC type or health does not match the schematic.");
            npc.position = new(x, y);
            npc.direction = record.Direction;
            npc.spriteDirection = record.SpriteDirection;
            if (record.Name is not null) npc.GivenName = record.Name;
            npc.homeless = record.Homeless;
            npc.homeTileX = record.Homeless ? 0 : bounds.X + record.HomeX;
            npc.homeTileY = record.Homeless ? 0 : bounds.Y + record.HomeY;
            if (record.LifeOverride is int overrideLife) npc.life = overrideLife;
            npc.netUpdate = true;
        }
    }

    private static void ApplyTiles(SceneTileArea area, SceneBounds bounds)
    {
        for (int y = 0; y < bounds.Height; y++)
        for (int x = 0; x < bounds.Width; x++)
            TerrariaWorldTileService.Apply(global::Terraria.Main.tile[bounds.X + x, bounds.Y + y], area[x, y]);
    }

    private static WorldSchematicPlacementResult Invalid(string error) => new(WorldSchematicPlacementStatus.Invalid, Error: error);
}
