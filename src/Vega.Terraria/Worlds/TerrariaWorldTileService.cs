using System.Collections.Concurrent;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldTileService(
    IGameThreadDispatcher dispatcher,
    WorldId worldId) : IWorldTileService
{
    private const int MaxTilesPerOperation = WorldTileOperationLimits.MaximumTiles;
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
    private readonly ConcurrentDictionary<AreaIdentity, VersionState> versions = new();

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
        if (requestedWorldId != worldId || !ValidateBounds(bounds, out _))
        {
            return null;
        }

        (SceneTileArea area, long fingerprint) = CaptureArea(global::Terraria.Main.tile, bounds);
        var identity = new AreaIdentity(requestedWorldId, bounds);
        VersionState state = versions.AddOrUpdate(
            identity,
            static (_, value) => new VersionState(1, value),
            static (_, current, value) => current.Fingerprint == value
                ? current
                : new VersionState(checked(current.Revision + 1), value),
            fingerprint);
        return new WorldTileAreaSnapshot(
            requestedWorldId,
            bounds,
            state.Revision,
            area,
            DateTimeOffset.UtcNow);
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
        if (request.WorldId != worldId)
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
            request.Tiles.Height != request.Bounds.Height)
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

        if (before.Revision != request.ExpectedRevision)
        {
            return new WorldTileMutationResult(
                WorldTileMutationStatus.RevisionConflict,
                before,
                ErrorCode: "world.tiles.revision_conflict");
        }

        for (var localY = 0; localY < request.Bounds.Height; localY++)
        {
            int worldY = request.Bounds.Y + localY;
            for (var localX = 0; localX < request.Bounds.Width; localX++)
            {
                int worldX = request.Bounds.X + localX;
                Apply(global::Terraria.Main.tile[worldX, worldY], request.Tiles[localX, localY]);
            }
        }

        if (global::Terraria.Main.netMode == 2)
        {
            global::Terraria.NetMessage.SendTileSquare(
                -1,
                request.Bounds.X,
                request.Bounds.Y,
                request.Bounds.Width,
                request.Bounds.Height);
        }

        WorldTileAreaSnapshot? after = Capture(request.WorldId, request.Bounds);
        return after is null
            ? new WorldTileMutationResult(
                WorldTileMutationStatus.Failed,
                before,
                ErrorCode: "world.tiles.capture_after_failed")
            : new WorldTileMutationResult(
                WorldTileMutationStatus.Applied,
                before,
                after);
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
        (tile.LiquidAmount == 0 ? tile.LiquidKind == 0 : tile.LiquidKind <= 3) &&
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
        tile.liquidType(value.LiquidAmount == 0 ? 0 : value.LiquidKind);
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
                () => completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled! : action())))
        {
            return ValueTask.FromResult(unavailable!);
        }

        return new ValueTask<T>(completion.Task);
    }

    private readonly record struct AreaIdentity(WorldId WorldId, SceneBounds Bounds);
    private sealed record VersionState(long Revision, long Fingerprint);
}
