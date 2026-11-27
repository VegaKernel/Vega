using Vega.Contracts.Runtime;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldTileService : IWorldTileService, IDisposable
{
    private const int MaxTilesPerOperation = WorldTileOperationLimits.MaximumTiles;
    // Revisions are opaque. Bound the number of retained selection shapes; an evicted
    // receipt conflicts rather than being able to match a recycled revision.
    internal const int MaximumTrackedAreas = 4096;
    private const SceneTileFlags KnownFlags =
        SceneTileFlags.Active |
        SceneTileFlags.WireRed |
        SceneTileFlags.WireBlue |
        SceneTileFlags.WireGreen |
        SceneTileFlags.WireYellow |
        SceneTileFlags.HalfBlock |
        SceneTileFlags.Actuator |
        SceneTileFlags.Inactive |
        SceneTileFlags.FullbrightBlock |
        SceneTileFlags.FullbrightWall |
        SceneTileFlags.InvisibleBlock |
        SceneTileFlags.InvisibleWall;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly WorldId worldId;
    private readonly IRuntimeLifecycle? lifecycle;
    private readonly Dictionary<SceneBounds, VersionState> versions = new();
    private readonly Queue<SceneBounds> areas = new();
    private object? observedGrid;
    private Guid? observedFile;
    private Guid worldInstanceId;
    private long nextRevision;
    private int lifecycleGeneration, observedGeneration;
    private bool disposed;

    public TerrariaWorldTileService(IGameThreadDispatcher dispatcher, WorldId worldId, IRuntimeLifecycle? lifecycle = null)
    {
        this.dispatcher = dispatcher;
        this.worldId = worldId;
        this.lifecycle = lifecycle;
        if (lifecycle is not null) lifecycle.Transitioned += OnLifecycle;
    }

    private void OnLifecycle(RuntimeLifecycleTransition transition)
    {
        if (transition.Stage is RuntimeLifecycleStage.WorldLoading or RuntimeLifecycleStage.Stopping)
            Interlocked.Increment(ref lifecycleGeneration);
    }

    private bool TryObserveWorld()
    {
        if (Volatile.Read(ref disposed) || lifecycle is not null &&
            (lifecycle.CurrentStage < RuntimeLifecycleStage.WorldReady || lifecycle.CurrentStage >= RuntimeLifecycleStage.Stopping) ||
            global::Terraria.Main.tile is not { } grid) return false;
        Guid? file = global::Terraria.Main.ActiveWorldFileData?.UniqueId;
        int generation = Volatile.Read(ref lifecycleGeneration);
        if (!ReferenceEquals(observedGrid, grid) || observedFile != file || observedGeneration != generation)
        {
            observedGrid = grid;
            observedFile = file;
            observedGeneration = generation;
            worldInstanceId = Guid.NewGuid();
            versions.Clear();
            areas.Clear();
        }
        return true;
    }

    public ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
        WorldId requestedWorldId,
        SceneBounds bounds,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "world.tiles.capture",
            () => Capture(requestedWorldId, bounds),
            cancellationToken);

    public ValueTask<WorldTileMutationResult> ReplaceAsync(
        WorldTileMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return DispatchAsync(
            "world.tiles.replace",
            () => Replace(request),
            cancellationToken,
            new WorldTileMutationResult(
                WorldTileMutationStatus.Failed,
                ErrorCode: "world.tiles.dispatcher_unavailable"),
            new WorldTileMutationResult(
                WorldTileMutationStatus.Cancelled,
                ErrorCode: "world.tiles.cancelled"));
    }

    private WorldTileAreaSnapshot? Capture(WorldId requestedWorldId, SceneBounds bounds)
    {
        dispatcher.AssertGameThread();
        if (requestedWorldId != worldId || !TryObserveWorld() || !ValidateBounds(bounds, out _))
        {
            return null;
        }

        (SceneTileArea area, long fingerprint) = CaptureArea(global::Terraria.Main.tile, bounds);
        if (!versions.TryGetValue(bounds, out VersionState state))
        {
            if (versions.Count == MaximumTrackedAreas) versions.Remove(areas.Dequeue());
            areas.Enqueue(bounds);
            versions[bounds] = state = new(checked(++nextRevision), fingerprint);
        }
        else if (state.Fingerprint != fingerprint)
            versions[bounds] = state = new(checked(++nextRevision), fingerprint);
        return new WorldTileAreaSnapshot(
            requestedWorldId,
            bounds,
            state.Revision,
            area,
            DateTimeOffset.UtcNow) { WorldInstanceId = worldInstanceId };
    }

    internal static (SceneTileArea Area, long Fingerprint) CaptureArea(
        global::TerrariaRuntime.ICollection<global::Terraria.ITile> nativeTiles, SceneBounds bounds)
    {
        var hash = new HashCode();
        hash.Add(bounds.Width);
        hash.Add(bounds.Height);
        SceneTileArea area = SceneTileArea.Create(bounds.Width, bounds.Height, bounds, (tiles, rectangle) =>
        {
            int index = 0;
            int bottom = rectangle.Bottom;
            int right = rectangle.Right;
            for (int y = rectangle.Y; y < bottom; y++)
            {
                for (int x = rectangle.X; x < right; x++)
                {
                    SceneTile tile = Snapshot(nativeTiles[x, y]);
                    tiles[index++] = tile;
                    hash.Add(tile);
                }
            }
        });

        return (area, hash.ToHashCode());
    }

    private WorldTileMutationResult Replace(WorldTileMutationRequest request)
    {
        dispatcher.AssertGameThread();
        if (request.WorldId != worldId || !TryObserveWorld())
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.Invalid,
                ErrorCode: "world.tiles.world_not_active");
        }

        if (!ValidateBounds(request.Bounds, out string? error))
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.Invalid,
                ErrorCode: error);
        }

        if (request.Tiles.Width != request.Bounds.Width ||
            request.Tiles.Height != request.Bounds.Height ||
            request.ExpectedTiles is { } expected && (expected.Width != request.Bounds.Width || expected.Height != request.Bounds.Height))
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.Invalid,
                ErrorCode: "world.tiles.dimension_mismatch");
        }

        if (request.Tiles.Tiles.Any(static tile => !IsValid(tile)))
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.Invalid,
                ErrorCode: "world.tiles.invalid_tile");
        }

        WorldTileAreaSnapshot? before = Capture(request.WorldId, request.Bounds);
        if (before is null)
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.NotFound,
                ErrorCode: "world.tiles.not_found");
        }

        if (before.Revision != request.ExpectedRevision ||
            (request.ExpectedTiles is { } original && (request.ExpectedWorldInstanceId != before.WorldInstanceId ||
                !TileContentsEqual(original, before.Tiles))) ||
            (request.ExpectedWorldInstanceId is { } instance && instance != before.WorldInstanceId))
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.RevisionConflict,
                before,
                ErrorCode: "world.tiles.revision_conflict");
        }

        var native = global::Terraria.Main.tile;
        WorldTileAreaSnapshot after;
        try
        {
            ApplyArea(native, request.Bounds, request.Tiles, before.Tiles);
            after = Capture(request.WorldId, request.Bounds) ?? throw new InvalidOperationException("Cannot capture completed tile edit.");
            if (after.WorldInstanceId != before.WorldInstanceId || !TileContentsEqual(request.Tiles, after.Tiles))
                throw new InvalidOperationException("World changed or tile provider rejected the requested tile edit.");
        }
        catch (Exception mutationError)
        {
            // No packets have been sent. Restore the original tile values before returning a failure.
            // If a native tile provider itself refuses rollback, propagate the consistency failure.
            try
            {
                ApplyArea(native, request.Bounds, before.Tiles);
                if (!TileContentsEqual(CaptureArea(native, request.Bounds).Area, before.Tiles))
                    throw new InvalidOperationException("Native tile provider did not restore the original tile snapshot.");
            }
            catch (Exception rollbackError) { throw new InvalidOperationException("Tile edit rollback failed.", new AggregateException(mutationError, rollbackError)); }
            return new(WorldTileMutationStatus.Failed, before, ErrorCode: "world.tiles.apply_failed");
        }

        // Authoritative edit is complete. A transport error cannot turn an already-applied edit
        // into an unapplied one or safely undo a partially broadcast update.
        string? syncError = null;
        if (global::Terraria.Main.netMode == 2)
        {
            try
            {
                const int side = Vega.Terraria.Scenes.TerrariaSceneRenderSink.MaximumTileSquareSide;
                for (int x = request.Bounds.X; x < request.Bounds.Right; x += side)
                    for (int y = request.Bounds.Y; y < request.Bounds.Bottom; y += side)
                        global::Terraria.NetMessage.SendTileSquare(-1, x, y,
                            Math.Min(side, request.Bounds.Right - x), Math.Min(side, request.Bounds.Bottom - y));
            }
            catch (Exception) { syncError = "world.tiles.broadcast_failed"; }
        }

        return new(WorldTileMutationStatus.Applied, before, after, syncError);
    }

    private static bool TileContentsEqual(SceneTileArea expected, SceneTileArea actual)
    {
        if (expected.Width != actual.Width || expected.Height != actual.Height) return false;
        for (int i = 0; i < expected.Tiles.Count; i++)
            if (expected.Tiles[i] != actual.Tiles[i]) return false;
        return true;
    }

    private static void ApplyArea(global::TerrariaRuntime.ICollection<global::Terraria.ITile> native,
        SceneBounds bounds, SceneTileArea tiles, SceneTileArea? previous = null)
    {
        for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
                if (previous is null || previous[x, y] != tiles[x, y]) Apply(native[bounds.X + x, bounds.Y + y], tiles[x, y]);
    }

    internal static bool ValidateBounds(SceneBounds bounds, out string? error)
    {
        error = null;
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            error = "world.tiles.invalid_bounds";
            return false;
        }

        long count = (long)bounds.Width * bounds.Height;
        if (count > MaxTilesPerOperation)
        {
            error = "world.tiles.area_too_large";
            return false;
        }

        if ((long)bounds.X + bounds.Width > global::Terraria.Main.maxTilesX ||
            (long)bounds.Y + bounds.Height > global::Terraria.Main.maxTilesY)
        {
            error = "world.tiles.out_of_world";
            return false;
        }

        return true;
    }

    internal static bool IsValid(SceneTile tile) =>
        tile.TileType < global::Terraria.ID.TileID.Count &&
        tile.WallType < global::Terraria.ID.WallID.Count &&
        tile.TileColor <= global::Terraria.Main.numTileColors &&
        tile.WallColor <= global::Terraria.Main.numTileColors &&
        tile.Slope <= 4 &&
        tile.LiquidKind <= 3 &&
        (tile.Flags & ~KnownFlags) == 0 &&
        (tile.Slope == 0 || (tile.Flags & SceneTileFlags.HalfBlock) == 0);

    internal static SceneTile Snapshot(global::Terraria.ITile tile)
    {
        SceneTileFlags flags = SceneTileFlags.None;
        if (tile.active()) flags |= SceneTileFlags.Active;
        if (tile.wire()) flags |= SceneTileFlags.WireRed;
        if (tile.wire2()) flags |= SceneTileFlags.WireBlue;
        if (tile.wire3()) flags |= SceneTileFlags.WireGreen;
        if (tile.wire4()) flags |= SceneTileFlags.WireYellow;
        if (tile.halfBrick()) flags |= SceneTileFlags.HalfBlock;
        if (tile.actuator()) flags |= SceneTileFlags.Actuator;
        if (tile.inActive()) flags |= SceneTileFlags.Inactive;
        if (tile.fullbrightBlock()) flags |= SceneTileFlags.FullbrightBlock;
        if (tile.fullbrightWall()) flags |= SceneTileFlags.FullbrightWall;
        if (tile.invisibleBlock()) flags |= SceneTileFlags.InvisibleBlock;
        if (tile.invisibleWall()) flags |= SceneTileFlags.InvisibleWall;

        return new SceneTile(
            tile.type,
            tile.wall,
            tile.frameX,
            tile.frameY,
            tile.liquid,
            tile.liquidType(),
            tile.color(),
            tile.wallColor(),
            tile.slope(),
            flags);
    }

    internal static void Apply(global::Terraria.ITile tile, SceneTile value)
    {
        tile.active((value.Flags & SceneTileFlags.Active) != 0);
        tile.type = value.TileType;
        tile.wall = value.WallType;
        tile.frameX = value.FrameX;
        tile.frameY = value.FrameY;
        tile.liquid = value.LiquidAmount;
        // Native tiles may retain their valid liquid type after the amount reaches zero.
        // Preserve that bit state so a captured tile area is round-trippable for undo.
        tile.liquidType(value.LiquidKind);
        tile.color(value.TileColor);
        tile.wallColor(value.WallColor);
        tile.slope(value.Slope);
        tile.halfBrick((value.Flags & SceneTileFlags.HalfBlock) != 0);
        tile.actuator((value.Flags & SceneTileFlags.Actuator) != 0);
        tile.inActive((value.Flags & SceneTileFlags.Inactive) != 0);
        tile.wire((value.Flags & SceneTileFlags.WireRed) != 0);
        tile.wire2((value.Flags & SceneTileFlags.WireBlue) != 0);
        tile.wire3((value.Flags & SceneTileFlags.WireGreen) != 0);
        tile.wire4((value.Flags & SceneTileFlags.WireYellow) != 0);
        tile.fullbrightBlock((value.Flags & SceneTileFlags.FullbrightBlock) != 0);
        tile.fullbrightWall((value.Flags & SceneTileFlags.FullbrightWall) != 0);
        tile.invisibleBlock((value.Flags & SceneTileFlags.InvisibleBlock) != 0);
        tile.invisibleWall((value.Flags & SceneTileFlags.InvisibleWall) != 0);
    }

    private ValueTask<T> DispatchAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken,
        T? unavailable = default,
        T? cancelled = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled!);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable!);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                operation,
                () =>
                {
                    try { completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action()); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                }))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }

    public void Dispose()
    {
        Volatile.Write(ref disposed, true);
        if (lifecycle is not null) lifecycle.Transitioned -= OnLifecycle;
    }

    private readonly record struct VersionState(long Revision, long Fingerprint);
}
