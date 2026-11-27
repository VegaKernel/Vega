using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    public bool SupportsVirtualDisplays => true;
    public bool HasVirtualDisplays(WorldId worldId) => ReadLookup().TryGetValue(worldId, out var world) && world.HasDisplays;

    public IReadOnlyList<SceneVirtualDisplaySnapshot> SnapshotVisibleVirtualDisplays(WorldId worldId, SceneSessionObserver observer, SceneBounds region)
    {
        if (region.Width <= 0 || region.Height <= 0 || !HasVirtualDisplays(worldId)) return [];
        lock (sync)
        {
            if (!sessions.TryGetValue((worldId, observer.Player), out var current) || current != observer.Session ||
                !ReadLookup().TryGetValue(worldId, out var world) || !world.HasDisplays) return [];
            var candidates = world.All;
            if (world.Chunks is { } chunks && (region.X >> ChunkShift) == ((region.Right - 1) >> ChunkShift) &&
                (region.Y >> ChunkShift) == ((region.Bottom - 1) >> ChunkShift) &&
                !chunks.TryGetValue((region.X >> ChunkShift, region.Y >> ChunkShift), out candidates!)) return [];
            List<SceneVirtualDisplaySnapshot>? result = null;
            foreach (var overlay in candidates)
            {
                if (overlay.VirtualDisplays.Count == 0 || !overlay.Bounds.Intersects(region) || !overlay.Observers.Contains(observer.Player)) continue;
                foreach (var display in overlay.VirtualDisplays)
                {
                    int x = overlay.Bounds.X + display.LocalX, y = overlay.Bounds.Y + display.LocalY;
                    if (!new SceneBounds(x, y, display.Width, display.Height).Intersects(region) ||
                        !FootprintVisible(world, observer.Player, overlay, display.LocalX, display.LocalY, display.Width, display.Height) ||
                        !TryFindOwningScene(worldId, overlay, out var scene)) continue;
                    (result ??= []).Add(new(scene, overlay.Id, worldId, new(x, y), display));
                }
            }
            return result is null ? [] : result.AsReadOnly();
        }
    }
}
