using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    private const int MaximumVirtualChestsPerOverlay = 256;
    private const double VirtualChestReachPixels = 8 * 16;
    private static readonly TimeSpan VirtualChestOpenLifetime = TimeSpan.FromSeconds(5);
    // Latest intent per exact live player slot; never a growing packet/event queue.
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualChestOpenRequest> pendingVirtualChestOpens = [];
    public bool SupportsVirtualChests => true;

    private static SceneOverlayDefinition FreezeVirtualChests(SceneOverlayDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var chests = definition.VirtualChests ?? throw new ArgumentException("Virtual chest collection is required.", nameof(definition));
        if (chests.Count == 0)
            return ReferenceEquals(chests, Array.Empty<SceneVirtualChestDefinition>()) ? definition :
                definition with { VirtualChests = Array.Empty<SceneVirtualChestDefinition>() };
        if (chests.Count > MaximumVirtualChestsPerOverlay || definition.Bounds.Width < 2 || definition.Bounds.Height < 2)
            throw new ArgumentException("Too many virtual chests in one provider.", nameof(definition));
        var frozen = chests.ToArray();
        if (frozen.Length > MaximumVirtualChestsPerOverlay) throw new ArgumentException("Too many virtual chests in one provider.", nameof(definition));
        var ids = new HashSet<VirtualChestId>();
        var occupied = new HashSet<(int X, int Y)>();
        foreach (var chest in frozen)
        {
            if (chest is null || !ids.Add(chest.Id) || chest.LocalX > definition.Bounds.Width - 2 ||
                chest.LocalY > definition.Bounds.Height - 2)
                throw new ArgumentException("Virtual chest must have a unique identity and fit inside its provider.", nameof(definition));
            for (int x = chest.LocalX; x < chest.LocalX + 2; x++)
            for (int y = chest.LocalY; y < chest.LocalY + 2; y++)
                if (!occupied.Add((x, y))) throw new ArgumentException("Virtual chest footprints cannot overlap.", nameof(definition));
        }
        return definition with { VirtualChests = Array.AsReadOnly(frozen) };
    }

    private static bool SameVisualProvider(SceneOverlayDefinition a, SceneOverlayDefinition b) =>
        a.Bounds == b.Bounds && a.Order == b.Order && a.Enabled == b.Enabled && a.FillOutsideWithAir == b.FillOutsideWithAir &&
        ReferenceEquals(a.Tiles, b.Tiles) && a.Observers.IsGlobal == b.Observers.IsGlobal &&
        a.Observers.Players.SetEquals(b.Observers.Players);

    public bool TryResolveVirtualChest(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualChestSnapshot? chest)
    {
        lock (sync) return TryResolveVirtualChestLocked(worldId, observer, tileX, tileY, out chest);
    }

    private bool TryResolveVirtualChestLocked(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualChestSnapshot? chest)
    {
        chest = null;
        if (!sessions.TryGetValue((worldId, observer.Player), out var current) || current != observer.Session ||
            !ReadLookup().TryGetValue(worldId, out var world)) return false;
        var definition = VisibleProvider(world, observer.Player, tileX, tileY);
        if (definition is null || definition.VirtualChests.Count == 0) return false;
        int localX = tileX - definition.Bounds.X, localY = tileY - definition.Bounds.Y;
        for (int i = 0; i < definition.VirtualChests.Count; i++)
        {
            var candidate = definition.VirtualChests[i];
            if (localX < candidate.LocalX || localX >= candidate.LocalX + 2 || localY < candidate.LocalY || localY >= candidate.LocalY + 2) continue;
            int x = definition.Bounds.X + candidate.LocalX, y = definition.Bounds.Y + candidate.LocalY;
            // Partial occlusion must not expose a hidden logical container.
            for (int dx = 0; dx < 2; dx++)
            for (int dy = 0; dy < 2; dy++)
                if (!ReferenceEquals(VisibleProvider(world, observer.Player, x + dx, y + dy), definition) ||
                    (definition.Tiles[candidate.LocalX + dx, candidate.LocalY + dy].Flags & SceneTileFlags.Active) == 0) return false;
            // Raw scene callers can supply their own overlay IDs; match the actual
            // winning immutable definition, not just an ID shared by another scene.
            // ponytail: scan scene owners only for container interactions; index
            // definition -> owning scene if measurements warrant it.
            foreach (var scene in scenes.Values)
            {
                if (scene.WorldId != worldId || !scene.Overlays.TryGetValue(definition.Id, out var state) ||
                    !ReferenceEquals(state.Definition, definition)) continue;
                chest = new(scene.Id, definition.Id, new(worldId, x, y), candidate);
                return true;
            }
            return false;
        }
        return false;
    }

    private static SceneOverlayDefinition? VisibleProvider(WorldLookup world, PlayerId observer, int x, int y)
    {
        bool masked = world.AirMasks?.ContainsKey(observer) == true;
        SceneOverlayDefinition[] candidates = world.All;
        if (world.Chunks is { } chunks && !chunks.TryGetValue((x >> ChunkShift, y >> ChunkShift), out candidates!)) return null;
        foreach (var candidate in candidates)
            if ((!masked || !candidate.Observers.IsGlobal) && candidate.Bounds.Contains(x, y) && candidate.Observers.Contains(observer))
                return candidate;
        return null;
    }

    public bool TryRequestVirtualChestOpen(PlayerSnapshot player, int tileX, int tileY)
    {
        if (player.SessionId is not { } session || player.IsDead || tileX < 0 || tileY < 0 ||
            !float.IsFinite(player.Position.X) || !float.IsFinite(player.Position.Y)) return false;
        var observer = new SceneSessionObserver(player.Id, session);
        lock (sync)
        {
            if (!TryResolveVirtualChestLocked(player.WorldId, observer, tileX, tileY, out var resolved) || resolved is not { } chest) return false;
            if (Math.Abs(player.Position.X - (chest.Identity.X * 16d + 16)) > VirtualChestReachPixels ||
                Math.Abs(player.Position.Y - (chest.Identity.Y * 16d + 16)) > VirtualChestReachPixels) return false;
            pendingVirtualChestOpens[(player.WorldId, player.Id)] = new(chest, observer, DateTimeOffset.UtcNow);
            return true;
        }
    }

    public bool TryTakeVirtualChestOpen(SceneId scene, OverlayId overlay, VirtualChestId chest,
        out SceneVirtualChestOpenRequest? request)
    {
        lock (sync)
        {
            while (true)
            {
                SceneVirtualChestOpenRequest? pending = null;
                foreach (var value in pendingVirtualChestOpens.Values)
                    if (value.Chest.SceneId == scene && value.Chest.OverlayId == overlay && value.Chest.Definition.Id == chest)
                    { pending = value; break; }
                if (pending is not { } intent) break;
                var identity = intent.Chest.Identity;
                pendingVirtualChestOpens.Remove((identity.WorldId, intent.Observer.Player));
                if (DateTimeOffset.UtcNow - intent.RequestedAt > VirtualChestOpenLifetime ||
                    !TryResolveVirtualChestLocked(identity.WorldId, intent.Observer, identity.X, identity.Y, out var resolved) ||
                    resolved is not { } current || current.SceneId != scene || current.OverlayId != overlay || current.Definition.Id != chest ||
                    current.Identity != identity) continue;
                request = intent with { Chest = current };
                return true;
            }
            request = null;
            return false;
        }
    }

    private void RemoveVirtualChestOpens(SceneId scene, OverlayId? overlay = null)
    {
        if (pendingVirtualChestOpens.Count == 0) return;
        foreach (var key in pendingVirtualChestOpens.Where(pair => pair.Value.Chest.SceneId == scene &&
                     (overlay is null || pair.Value.Chest.OverlayId == overlay.Value)).Select(pair => pair.Key).ToArray())
            pendingVirtualChestOpens.Remove(key);
    }
}
