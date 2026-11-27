using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Curated access to the world hosted by the current Vega process.</summary>
public interface IPluginWorld
{
    WorldId Current { get; }
    IPluginWorldEnvironment Environment { get; }
    IPluginWorldTiles Tiles { get; }
    IPluginWorldChests Chests { get; }
    IPluginWorldNpcs Npcs { get; }
    IPluginWorldScenes Scenes { get; }
    IPluginWorldSchematics Schematics => throw new NotSupportedException("World schematic capture is unavailable in this host.");
    IPluginFakeProvider FakeProvider => throw new NotSupportedException("FakeProvider is unavailable in this host.");
    IPluginRegions Regions => throw new NotSupportedException("Region access is unavailable in this host.");
}

/// <summary>Detached region snapshots and audited optimistic mutations in the hosted world only.</summary>
public interface IPluginRegions
{
    bool IsAvailable { get; }
    IReadOnlyList<RegionSnapshot> Snapshot();
    RegionSnapshot? FindTop(int tileX, int tileY);
    /// <summary>All overlapping regions at the tile, ordered by the native protection-region precedence.</summary>
    IReadOnlyList<RegionSnapshot> FindContaining(int tileX, int tileY) => Array.AsReadOnly(Snapshot()
        .Where(region => region.Contains(tileX, tileY)).OrderByDescending(region => region.Priority)
        .ThenBy(region => (long)region.Width * region.Height).ThenBy(region => region.Id).ToArray());
    /// <summary>Creates a protected region using current-world bounds. The host supplies the audit actor.</summary>
    ValueTask<RegionMutationResult> CreateAsync(string name, int x, int y, int width, int height, int priority = 0,
        string? owner = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RegionMutationResult(false, "region.mutation-unavailable"));
    /// <summary>Pass the observed region including its stable ID and revision. Obtain a fresh snapshot on conflict.</summary>
    ValueTask<RegionMutationResult> UpdateAsync(RegionSnapshot expected, RegionUpdate update, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RegionMutationResult(false, "region.mutation-unavailable"));
    ValueTask<RegionMutationResult> RenameAsync(RegionSnapshot expected, string newName, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RegionMutationResult(false, "region.mutation-unavailable"));
    ValueTask<RegionMutationResult> SetAccessAsync(RegionSnapshot expected, RegionAccessKind kind, string subject, bool enabled,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(new RegionMutationResult(false, "region.mutation-unavailable"));
    ValueTask<RegionMutationResult> DeleteAsync(RegionSnapshot expected, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new RegionMutationResult(false, "region.mutation-unavailable"));
}

public interface IPluginWorldEnvironment
{
    ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);

    ValueTask<WorldEnvironmentMutationResult> PatchAsync(
        WorldEnvironmentPatch patch,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldTiles
{
    ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
        SceneBounds bounds,
        CancellationToken cancellationToken = default);

    ValueTask<WorldTileMutationResult> ReplaceAsync(
        WorldTileAreaSnapshot expected,
        SceneTileArea replacement,
        CancellationToken cancellationToken = default);

    /// <summary>Inverts a successful edit only if its exact after-snapshot is still current. Invert the resulting
    /// receipt again for redo. No implicit history is retained by the host.</summary>
    ValueTask<WorldTileMutationResult> UndoAsync(WorldTileMutationResult edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!edit.IsSuccess || edit.Before is not { } before || edit.After is not { } after ||
            before.WorldId != after.WorldId || before.Bounds != after.Bounds || before.WorldInstanceId != after.WorldInstanceId)
            return ValueTask.FromResult(new WorldTileMutationResult(WorldTileMutationStatus.Invalid, ErrorCode: "world.tiles.invalid_receipt"));
        return ReplaceAsync(after, before.Tiles, cancellationToken);
    }
}

public interface IPluginWorldChests
{
    ValueTask<ChestSnapshot?> CaptureAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default);

    ValueTask<ChestMutationResult> ReplaceAsync(
        ChestSnapshot expected,
        IReadOnlyList<ChestItemSnapshot> items,
        string? name = null,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldNpcs
{
    ValueTask<NpcActorCreationResult> CreateActorAsync(NpcActorDefinition definition, PlayerPosition position,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(new NpcActorCreationResult(NpcOperationStatus.DispatcherUnavailable));
    ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<NpcSnapshot?> CaptureAsync(
        NpcHandle handle,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> SpawnAsync(
        int type,
        PlayerPosition position,
        int? targetPlayerSlot = null,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> TeleportAsync(
        NpcHandle expected,
        PlayerPosition position,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> DespawnAsync(
        NpcHandle expected,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldScenes
{
    /// <summary>Creates a plugin-owned scene which is automatically detached on plugin unload.</summary>
    ISceneLease Create(string name);
}

public interface IPluginWorldSchematics
{
    /// <summary>Captures current world data and encodes the portable schematic format outside the game thread.</summary>
    ValueTask<WorldSchematicCaptureResult> CaptureAsync(SceneBounds bounds, CancellationToken cancellationToken = default);
    /// <summary>Materializes supported records into an empty, isolated sandbox; unsupported documents are refused.</summary>
    ValueTask<WorldSchematicPlacementResult> PlaceAsync(ReadOnlyMemory<byte> data, int x, int y, CancellationToken cancellationToken = default);
}

public interface IPluginPlayerInventory
{
    ValueTask<PlayerInventorySnapshot?> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default);

    /// <summary>Capture only the originally observed player session.</summary>
    ValueTask<PlayerInventorySnapshot?> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<PlayerInventorySnapshot?>(null);

    ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerInventorySnapshot expected,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default);
}
