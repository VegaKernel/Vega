using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    public bool SupportsVirtualItemFrames => true;

    public bool HasVirtualItemFrames(WorldId worldId) => ReadLookup().TryGetValue(worldId, out var world) && world.HasItemFrames;

    private static bool ValidItemFrameTiles(SceneTileArea tiles, int localX, int localY) =>
        ValidDisplayTiles(tiles, localX, localY, SceneVirtualItemFrameDefinition.TileType, 2, 2);

    private static bool ValidDisplayTiles(SceneTileArea tiles, int localX, int localY, ushort tileType, int width, int height)
    {
        if (localX > tiles.Width - width || localY > tiles.Height - height) return false;
        var anchor = tiles[localX, localY];
        if (anchor.FrameX < 0 || anchor.FrameX % (18 * width) != 0 || anchor.FrameY != 0) return false;
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            var tile = tiles[localX + x, localY + y];
            if ((tile.Flags & SceneTileFlags.Active) == 0 || tile.TileType != tileType ||
                tile.FrameX != anchor.FrameX + x * 18 || tile.FrameY != y * 18) return false;
        }
        return true;
    }

    public IReadOnlyList<SceneVirtualItemFrameSnapshot> SnapshotVisibleVirtualItemFrames(WorldId worldId, SceneSessionObserver observer, SceneBounds region)
    {
        if (region.Width <= 0 || region.Height <= 0 || !HasVirtualItemFrames(worldId)) return [];
        lock (sync)
        {
            if (!sessions.TryGetValue((worldId, observer.Player), out var current) || current != observer.Session ||
                !ReadLookup().TryGetValue(worldId, out var world) || !world.HasItemFrames) return [];
            var candidates = world.All;
            if (world.Chunks is { } chunks && (region.X >> ChunkShift) == ((region.Right - 1) >> ChunkShift) &&
                (region.Y >> ChunkShift) == ((region.Bottom - 1) >> ChunkShift) &&
                !chunks.TryGetValue((region.X >> ChunkShift, region.Y >> ChunkShift), out candidates!)) return [];
            List<SceneVirtualItemFrameSnapshot>? result = null;
            foreach (var overlay in candidates)
            {
                if (overlay.VirtualItemFrames.Count == 0 || !overlay.Bounds.Intersects(region) ||
                    !overlay.Observers.Contains(observer.Player)) continue;
                foreach (var frame in overlay.VirtualItemFrames)
                {
                    int x = overlay.Bounds.X + frame.LocalX, y = overlay.Bounds.Y + frame.LocalY;
                    if (!new SceneBounds(x, y, 2, 2).Intersects(region) || !FootprintVisible(world, observer.Player, overlay, frame.LocalX, frame.LocalY) ||
                        !TryFindOwningScene(worldId, overlay, out var scene)) continue;
                    (result ??= []).Add(new(scene, overlay.Id, worldId, new(x, y), frame));
                }
            }
            return result is null ? [] : result.AsReadOnly();
        }
    }
}
