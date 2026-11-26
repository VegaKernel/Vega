using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    private const int MaximumVirtualObjectsPerOverlay = 256;
    private const double VirtualObjectReachPixels = 8 * 16;
    private static readonly TimeSpan VirtualInteractionLifetime = TimeSpan.FromSeconds(5);
    // Latest intent per exact live player slot; never a growing packet/event queue.
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualChestOpenRequest> pendingVirtualChestOpens = [];
    public bool SupportsVirtualChests => true;

    private static bool HasVirtualObjects(SceneOverlayDefinition definition) => definition.VirtualChests.Count != 0 || definition.VirtualSigns.Count != 0;

    private static SceneOverlayDefinition FreezeVirtualObjects(SceneOverlayDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var chests = definition.VirtualChests;
        var signs = definition.VirtualSigns;
        if (chests.Count == 0 && signs.Count == 0)
            return ReferenceEquals(chests, Array.Empty<SceneVirtualChestDefinition>()) && ReferenceEquals(signs, Array.Empty<SceneVirtualSignDefinition>())
                ? definition : definition with { VirtualChests = Array.Empty<SceneVirtualChestDefinition>(), VirtualSigns = Array.Empty<SceneVirtualSignDefinition>() };
        if ((long)chests.Count + signs.Count > MaximumVirtualObjectsPerOverlay || definition.Bounds.Width < 2 || definition.Bounds.Height < 2)
            throw new ArgumentException("Too many virtual objects or invalid provider bounds.", nameof(definition));
        var frozenChests = chests.ToArray();
        var frozenSigns = signs.ToArray();
        if ((long)frozenChests.Length + frozenSigns.Length > MaximumVirtualObjectsPerOverlay)
            throw new ArgumentException("Too many virtual objects in one provider.", nameof(definition));
        var chestIds = new HashSet<VirtualChestId>();
        var signIds = new HashSet<VirtualSignId>();
        var occupied = new HashSet<(int X, int Y)>();
        foreach (var chest in frozenChests)
        {
            if (chest is null || !chestIds.Add(chest.Id)) throw new ArgumentException("Virtual chest identity must be unique.", nameof(definition));
            ValidateFootprint(definition.Bounds, chest.LocalX, chest.LocalY, occupied);
        }
        foreach (var sign in frozenSigns)
        {
            if (sign is null || !signIds.Add(sign.Id)) throw new ArgumentException("Virtual sign identity must be unique.", nameof(definition));
            ValidateFootprint(definition.Bounds, sign.LocalX, sign.LocalY, occupied);
        }
        return definition with
        {
            VirtualChests = frozenChests.Length == 0 ? Array.Empty<SceneVirtualChestDefinition>() : Array.AsReadOnly(frozenChests),
            VirtualSigns = frozenSigns.Length == 0 ? Array.Empty<SceneVirtualSignDefinition>() : Array.AsReadOnly(frozenSigns)
        };
    }

    private static void ValidateFootprint(SceneBounds bounds, int localX, int localY, HashSet<(int X, int Y)> occupied)
    {
        if (localX > bounds.Width - 2 || localY > bounds.Height - 2)
            throw new ArgumentException("Virtual object footprint must fit inside its provider.");
        for (int x = localX; x < localX + 2; x++)
        for (int y = localY; y < localY + 2; y++)
            if (!occupied.Add((x, y))) throw new ArgumentException("Virtual object footprints cannot overlap.");
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
            if (!FootprintVisible(world, observer.Player, definition, candidate.LocalX, candidate.LocalY) ||
                !TryFindOwningScene(worldId, definition, out SceneId scene)) return false;
            chest = new(scene, definition.Id, new(worldId, definition.Bounds.X + candidate.LocalX, definition.Bounds.Y + candidate.LocalY), candidate);
            return true;
        }
        return false;
    }

    private static bool FootprintVisible(WorldLookup world, PlayerId observer, SceneOverlayDefinition definition, int localX, int localY)
    {
        int x = definition.Bounds.X + localX, y = definition.Bounds.Y + localY;
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
            if (!ReferenceEquals(VisibleProvider(world, observer, x + dx, y + dy), definition) ||
                (definition.Tiles[localX + dx, localY + dy].Flags & SceneTileFlags.Active) == 0) return false;
        return true;
    }

    private bool TryFindOwningScene(WorldId worldId, SceneOverlayDefinition definition, out SceneId id)
    {
        // Match the actual published definition, not a raw overlay ID shared by another scene.
        // ponytail: scan scene owners only for object interactions; index definition -> scene if measurements warrant it.
        foreach (var scene in scenes.Values)
        {
            if (scene.WorldId != worldId || !scene.Overlays.TryGetValue(definition.Id, out var state) ||
                !ReferenceEquals(state.Definition, definition)) continue;
            id = scene.Id;
            return true;
        }
        id = default;
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
        if (!TryGetInteractionObserver(player, tileX, tileY, out var observer)) return false;
        lock (sync)
        {
            if (!TryResolveVirtualChestLocked(player.WorldId, observer, tileX, tileY, out var resolved) || resolved is not { } chest) return false;
            if (!WithinObjectReach(player, chest.Identity.X, chest.Identity.Y)) return false;
            pendingVirtualChestOpens[(player.WorldId, player.Id)] = new(chest, observer, DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static bool TryGetInteractionObserver(PlayerSnapshot player, int tileX, int tileY, out SceneSessionObserver observer)
    {
        observer = default;
        if (player.SessionId is not { } session || player.IsDead || tileX < 0 || tileY < 0 ||
            !float.IsFinite(player.Position.X) || !float.IsFinite(player.Position.Y)) return false;
        observer = new(player.Id, session);
        return true;
    }

    private static bool WithinObjectReach(PlayerSnapshot player, int x, int y) =>
        Math.Abs(player.Position.X - (x * 16d + 16)) <= VirtualObjectReachPixels &&
        Math.Abs(player.Position.Y - (y * 16d + 16)) <= VirtualObjectReachPixels;

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
                if (DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime ||
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
        List<(WorldId World, PlayerId Player)>? removed = null;
        foreach (var pair in pendingVirtualChestOpens)
            if (pair.Value.Chest.SceneId == scene && (overlay is null || pair.Value.Chest.OverlayId == overlay.Value))
                (removed ??= []).Add(pair.Key);
        if (removed is not null)
            foreach (var key in removed) pendingVirtualChestOpens.Remove(key);
    }
}
