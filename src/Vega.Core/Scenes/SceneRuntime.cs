using System.Collections.Frozen;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime(ISceneRenderSink? sink = null) : ISceneRuntime
{
    private const int MaximumDirtyRegions = 128;
    private const int MaximumIndexedChunkEntries = 16_384;
    private const int ChunkShift = 6;
    private readonly object sync = new();
    private readonly Dictionary<SceneId, SceneState> scenes = [];
    private readonly Dictionary<(WorldId World, PlayerId Player), PlayerSessionId> sessions = [];
    private Dictionary<WorldId, WorldLookup>? lookup;
    private long nextSequence;

    public bool HasActiveOverlays => ReadLookup().Count != 0;

    public bool HasPersonalAirMask(WorldId worldId, PlayerId observer) =>
        ReadLookup().TryGetValue(worldId, out var world) && world.AirMasks?.ContainsKey(observer) == true;

    public bool HasOverlayInRegion(WorldId worldId, SceneBounds region, PlayerId? observer = null)
    {
        if (region.Width <= 0 || region.Height <= 0) return false;
        if (!ReadLookup().TryGetValue(worldId, out var world)) return false;
        if (world.AirMasks is { Count: > 0 } masks &&
            (observer is null || masks.ContainsKey(observer.Value))) return true;

        if (world.Chunks is not { } chunks) return AnyIntersection(world.All, region, observer);
        int firstX = region.X >> ChunkShift, lastX = (region.Right - 1) >> ChunkShift;
        int firstY = region.Y >> ChunkShift, lastY = (region.Bottom - 1) >> ChunkShift;
        if (firstX == lastX && firstY == lastY)
            return chunks.TryGetValue((firstX, firstY), out var single) && AnyIntersection(single, region, observer);
        long buckets = (long)(lastX - firstX + 1) * (lastY - firstY + 1);
        // Tiny native tile-square probes normally occupy just one or a few buckets.
        // For broad regions the bounded flat scan avoids visiting many empty buckets.
        if (buckets <= Math.Min(16, world.All.Length))
        {
            for (int x = firstX; x <= lastX; x++)
            for (int y = firstY; y <= lastY; y++)
                if (chunks.TryGetValue((x, y), out var candidates) && AnyIntersection(candidates, region, observer))
                    return true;
            return false;
        }
        return AnyIntersection(world.All, region, observer);
    }

    private static bool AnyIntersection(SceneOverlayDefinition[] candidates, SceneBounds region, PlayerId? observer)
    {
        foreach (var overlay in candidates)
        {
            if (overlay.Bounds.Intersects(region) &&
                (observer is null || overlay.Observers.Contains(observer.Value))) return true;
        }
        return false;
    }

    public IEnumerable<SceneOverlayDefinition> EnumerateOverlaysInRegion(WorldId worldId, SceneBounds region)
    {
        if (region.Width <= 0 || region.Height <= 0 || !ReadLookup().TryGetValue(worldId, out var world)) yield break;
        SceneOverlayDefinition[] candidates = world.All;
        if (world.Chunks is { } chunks && (region.X >> ChunkShift) == ((region.Right - 1) >> ChunkShift) &&
            (region.Y >> ChunkShift) == ((region.Bottom - 1) >> ChunkShift) &&
            !chunks.TryGetValue((region.X >> ChunkShift, region.Y >> ChunkShift), out candidates!)) yield break;
        // Multiple buckets may contain the same overlay: traverse the immutable
        // flat projection once rather than allocating a deduplication set.
        foreach (var overlay in candidates)
            if (overlay.Bounds.Intersects(region)) yield return overlay;
    }

    public void OpenSession(WorldId world, PlayerSessionOpened opened)
    {
        lock (sync)
        {
            var key = (world, opened.PlayerId);
            if (sessions.TryGetValue(key, out var previous) && previous != opened.SessionId)
                RemoveObserver(world, opened.PlayerId);
            sessions[key] = opened.SessionId;
        }
    }

    public void CloseSession(WorldId world, PlayerSessionClosed closed)
    {
        lock (sync)
        {
            var key = (world, closed.PlayerId);
            if (!sessions.TryGetValue(key, out var current) || current != closed.SessionId) return;
            sessions.Remove(key);
            RemoveObserver(world, closed.PlayerId);
        }
    }

    private void RemoveObserver(WorldId world, PlayerId player)
    {
        pendingVirtualChestOpens.Remove((world, player));
        foreach (var scene in scenes.Values)
        {
            if (scene.WorldId != world) continue;
            foreach (var pair in scene.Overlays.ToArray())
            {
                var definition = pair.Value.Definition;
                if (definition.Observers.IsGlobal || !definition.Observers.Contains(player)) continue;
                var scope = SceneObserverScope.Personal(definition.Observers.Players.Where(id => id != player))
                    with { Sessions = definition.Observers.Sessions };
                scene.Overlays[pair.Key] = pair.Value with { Definition = definition with { Observers = scope } };
                Volatile.Write(ref lookup, null);
            }
        }
    }

    public ISceneLease CreateScene(WorldId worldId, string name, string owner)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Scene name and owner are required.");
        }

        var state = new SceneState(SceneId.New(), worldId, name.Trim(), owner.Trim());
        lock (sync)
        {
            scenes.Add(state.Id, state);
        }

        return new Lease(this, state.Id);
    }

    public IReadOnlyList<SceneSnapshot> SnapshotScenes()
    {
        lock (sync)
        {
            return Array.AsReadOnly(scenes.Values
                .OrderBy(static scene => scene.WorldId.Value, StringComparer.Ordinal)
                .ThenBy(static scene => scene.Name, StringComparer.Ordinal)
                .Select(Snapshot)
                .ToArray());
        }
    }

    public void ReportFailure(SceneId sceneId, string? failure)
    {
        if (sceneId.Value == Guid.Empty)
        {
            return;
        }

        lock (sync)
        {
            if (scenes.TryGetValue(sceneId, out SceneState? scene))
            {
                scene.Failure = string.IsNullOrWhiteSpace(failure) ? null : failure.Trim();
            }
        }
    }

    public bool TryResolveTile(
        WorldId worldId,
        PlayerId observer,
        int tileX,
        int tileY,
        out SceneTile tile,
        out OverlayId overlayId)
    {
        if (ReadLookup().TryGetValue(worldId, out WorldLookup? world))
        {
            OverlayId mask = default;
            bool masked = world.AirMasks?.TryGetValue(observer, out mask) == true;
            SceneOverlayDefinition[] candidates = world.All;
            if (world.Chunks is not null && !world.Chunks.TryGetValue((tileX >> ChunkShift, tileY >> ChunkShift), out candidates!))
                candidates = [];
            foreach (SceneOverlayDefinition candidate in candidates)
            {
                SceneBounds bounds = candidate.Bounds;
                if ((!masked || !candidate.Observers.IsGlobal) && bounds.Contains(tileX, tileY) && candidate.Observers.Contains(observer))
                {
                    tile = candidate.Tiles[tileX - bounds.X, tileY - bounds.Y];
                    overlayId = candidate.Id;
                    return true;
                }
            }
            if (masked)
            {
                tile = SceneTile.Air;
                overlayId = mask;
                return true;
            }
        }

        tile = default;
        overlayId = default;
        return false;
    }

    // Mutations invalidate the immutable projection. Rebuild once on the next read,
    // so large batches of overlay updates do not repeatedly sort the same scene set.
    // Readers never take the mutation lock per tile once the index is published.
    private Dictionary<WorldId, WorldLookup> ReadLookup()
    {
        var current = Volatile.Read(ref lookup);
        if (current is not null) return current;
        lock (sync)
        {
            current = lookup;
            if (current is not null) return current;
            current = scenes.Values
                .SelectMany(scene => scene.Overlays.Values
                    .Where(static overlay => overlay.Definition.Enabled)
                    .Select(overlay => (scene.WorldId, Overlay: overlay)))
                .GroupBy(static value => value.WorldId)
                .ToDictionary(static group => group.Key, static group => BuildWorldLookup(group
                    .OrderByDescending(static value => value.Overlay.Definition.Order)
                    .ThenByDescending(static value => value.Overlay.Sequence)
                    .Select(static value => value.Overlay.Definition).ToArray()));
            Volatile.Write(ref lookup, current);
            return current;
        }
    }

    private static WorldLookup BuildWorldLookup(SceneOverlayDefinition[] sorted)
    {
        Dictionary<PlayerId, OverlayId>? masks = null;
        foreach (var overlay in sorted)
        {
            if (!overlay.FillOutsideWithAir) continue;
            foreach (PlayerId observer in overlay.Observers.Players)
                (masks ??= []).TryAdd(observer, overlay.Id);
        }
        if (sorted.Length < 8) return new(sorted, null, masks);

        // Sparse 64x64 buckets, not a dimensions-sized tile map. Bound memory for
        // large/overlapping overlays; oversized sets retain the priority-sorted scan.
        long entries = 0;
        foreach (var overlay in sorted)
        {
            var bounds = overlay.Bounds;
            entries += (long)(((bounds.Right - 1) >> ChunkShift) - (bounds.X >> ChunkShift) + 1) *
                (((bounds.Bottom - 1) >> ChunkShift) - (bounds.Y >> ChunkShift) + 1);
            if (entries > MaximumIndexedChunkEntries) return new(sorted, null, masks);
        }

        var chunks = new Dictionary<(int X, int Y), List<SceneOverlayDefinition>>();
        // Appending in priority order preserves both layer and stable sequence tie-breaks.
        foreach (var overlay in sorted)
        {
            var bounds = overlay.Bounds;
            for (int x = bounds.X >> ChunkShift; x <= (bounds.Right - 1) >> ChunkShift; x++)
            for (int y = bounds.Y >> ChunkShift; y <= (bounds.Bottom - 1) >> ChunkShift; y++)
            {
                if (!chunks.TryGetValue((x, y), out var bucket)) chunks.Add((x, y), bucket = []);
                bucket.Add(overlay);
            }
        }
        return new(sorted, chunks.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray()), masks);
    }

    private sealed record WorldLookup(SceneOverlayDefinition[] All, Dictionary<(int X, int Y), SceneOverlayDefinition[]>? Chunks,
        Dictionary<PlayerId, OverlayId>? AirMasks);

    private OverlayId Add(SceneId sceneId, SceneOverlayDefinition definition)
    {
        definition = FreezeVirtualChests(definition);
        Validate(definition);
        SceneRenderRequest request;
        lock (sync)
        {
            SceneState scene = Get(sceneId);
            OverlayId id = definition.Id.Value == Guid.Empty ? OverlayId.New() : definition.Id;
            if (scene.Overlays.ContainsKey(id))
            {
                throw new InvalidOperationException($"Overlay '{id}' already exists.");
            }

            SceneOverlayDefinition stored = definition with { Id = id, Observers = FreezeScope(scene.WorldId, definition.Observers) };
            scene.Overlays.Add(id, new OverlayState(stored, ++nextSequence));
            Volatile.Write(ref lookup, null);
            RecordDirty(scene, stored.Bounds);
            request = RenderRequest(scene, stored.Bounds, stored.Observers, IsAirMask(stored));
            definition = stored;
        }

        sink?.Invalidate(request);
        return definition.Id;
    }

    private bool Update(SceneId sceneId, OverlayId overlayId, SceneOverlayDefinition definition)
    {
        definition = FreezeVirtualChests(definition);
        Validate(definition);
        SceneRenderRequest[] requests;
        lock (sync)
        {
            SceneState scene = Get(sceneId);
            if (!scene.Overlays.TryGetValue(overlayId, out OverlayState? current))
            {
                return false;
            }

            SceneOverlayDefinition stored = definition with { Id = overlayId, Observers = FreezeScope(scene.WorldId, definition.Observers) };
            scene.Overlays[overlayId] = new OverlayState(stored, current.Sequence);
            Volatile.Write(ref lookup, null);
            // Logical inventory changes must not enqueue tile data when the visible
            // provider has not changed; the chest snapshots are published separately.
            if ((stored.VirtualChests.Count != 0 || current.Definition.VirtualChests.Count != 0) &&
                SameVisualProvider(current.Definition, stored)) return true;
            RemoveVirtualChestOpens(sceneId, overlayId);
            RecordDirty(scene, current.Definition.Bounds);
            if (current.Definition.Bounds == stored.Bounds)
            {
                // A frame/content update within the same rectangle needs one redraw.
                // Include removed and added observers so no client keeps the old frame.
                var oldScope = current.Definition.Observers;
                var newScope = stored.Observers;
                var affected = oldScope.IsGlobal || newScope.IsGlobal ? SceneObserverScope.Global
                    : oldScope.Players.Count >= newScope.Players.Count && oldScope.Players.IsSupersetOf(newScope.Players) ? oldScope
                    : newScope.Players.Count >= oldScope.Players.Count && newScope.Players.IsSupersetOf(oldScope.Players) ? newScope
                    : SceneObserverScope.Personal(oldScope.Players.Concat(newScope.Players));
                requests = [RenderRequest(scene, stored.Bounds, affected)];
            }
            else
            {
                RecordDirty(scene, stored.Bounds);
                requests =
                [
                    RenderRequest(scene, current.Definition.Bounds, current.Definition.Observers),
                    RenderRequest(scene, stored.Bounds, stored.Observers)
                ];
            }
            // Only membership changes require repainting the whole loaded background.
            // Moving or animating an already enabled mask uses normal dirty rectangles.
            IReadOnlySet<PlayerId> oldMasks = IsAirMask(current.Definition)
                ? current.Definition.Observers.Players : SceneObserverScope.Global.Players;
            IReadOnlySet<PlayerId> newMasks = IsAirMask(stored)
                ? stored.Observers.Players : SceneObserverScope.Global.Players;
            if (!oldMasks.SetEquals(newMasks))
            {
                var changed = oldMasks.Where(player => !newMasks.Contains(player))
                    .Concat(newMasks.Where(player => !oldMasks.Contains(player)));
                requests = [.. requests, RenderRequest(scene, stored.Bounds, SceneObserverScope.Personal(changed), true)];
            }
        }

        foreach (SceneRenderRequest request in requests)
        {
            sink?.Invalidate(request);
        }

        return true;
    }

    private bool Remove(SceneId sceneId, OverlayId overlayId)
    {
        SceneRenderRequest request;
        lock (sync)
        {
            SceneState scene = Get(sceneId);
            if (!scene.Overlays.Remove(overlayId, out OverlayState? removed))
            {
                return false;
            }
            RemoveVirtualChestOpens(sceneId, overlayId);
            Volatile.Write(ref lookup, null);

            RecordDirty(scene, removed.Definition.Bounds);
            request = RenderRequest(scene, removed.Definition.Bounds, removed.Definition.Observers, IsAirMask(removed.Definition));
        }

        sink?.Invalidate(request);
        return true;
    }

    private bool InvalidateOverlay(SceneId sceneId, OverlayId overlayId, bool bringToFront)
    {
        SceneRenderRequest request;
        lock (sync)
        {
            SceneState scene = Get(sceneId);
            if (!scene.Overlays.TryGetValue(overlayId, out var overlay)) return false;
            if (bringToFront)
            {
                scene.Overlays[overlayId] = overlay with { Sequence = ++nextSequence };
                Volatile.Write(ref lookup, null);
            }
            RecordDirty(scene, overlay.Definition.Bounds);
            request = RenderRequest(scene, overlay.Definition.Bounds, overlay.Definition.Observers);
        }
        sink?.Invalidate(request);
        return true;
    }

    private void RemoveScene(SceneId sceneId)
    {
        SceneRenderRequest[] requests;
        lock (sync)
        {
            if (!scenes.Remove(sceneId, out SceneState? scene))
            {
                return;
            }
            RemoveVirtualChestOpens(sceneId);
            Volatile.Write(ref lookup, null);

            requests = scene.Overlays.Values
                .Select(overlay => RenderRequest(scene, overlay.Definition.Bounds, overlay.Definition.Observers, IsAirMask(overlay.Definition)))
                .ToArray();
        }

        foreach (SceneRenderRequest request in requests)
        {
            sink?.Invalidate(request);
        }
    }

    private SceneState Get(SceneId sceneId) => scenes.TryGetValue(sceneId, out SceneState? scene)
        ? scene
        : throw new ObjectDisposedException($"Scene {sceneId.Value}");

    private static void Validate(SceneOverlayDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name) ||
            definition.Bounds.Width <= 0 || definition.Bounds.Height <= 0 ||
            definition.Bounds.Width != definition.Tiles.Width ||
            definition.Bounds.Height != definition.Tiles.Height)
        {
            throw new ArgumentException("Overlay name, bounds and tile dimensions must be valid.", nameof(definition));
        }
        _ = definition.Bounds.Right;
        _ = definition.Bounds.Bottom;
        if (definition.FillOutsideWithAir && definition.Observers.IsGlobal)
            throw new ArgumentException("An air background is only available for personal overlays.", nameof(definition));
    }

    private SceneObserverScope FreezeScope(WorldId world, SceneObserverScope scope)
    {
        if (scope.IsGlobal) return SceneObserverScope.Global;
        if (scope.Sessions is { } requested)
        {
            var frozen = requested.ToFrozenDictionary();
            return SceneObserverScope.Personal(frozen.Where(pair =>
                sessions.TryGetValue((world, pair.Key), out var session) && session == pair.Value).Select(static pair => pair.Key))
                with { Sessions = frozen };
        }
        return scope.Players is FrozenSet<PlayerId> ? scope : SceneObserverScope.Personal(scope.Players);
    }

    private static SceneSnapshot Snapshot(SceneState scene)
    {
        SceneOverlayDefinition[] overlays = scene.Overlays.Values
            .OrderBy(static overlay => overlay.Definition.Order)
            .ThenBy(static overlay => overlay.Sequence)
            .Select(static overlay => overlay.Definition)
            .ToArray();
        int conflicts = 0;
        for (var first = 0; first < overlays.Length; first++)
        {
            for (var second = first + 1; second < overlays.Length; second++)
            {
                if (overlays[first].Enabled && overlays[second].Enabled &&
                    overlays[first].Order == overlays[second].Order &&
                    overlays[first].Bounds.Intersects(overlays[second].Bounds) &&
                    ObserverScopesOverlap(overlays[first].Observers, overlays[second].Observers))
                {
                    conflicts++;
                }
            }
        }

        return new SceneSnapshot(
            scene.Id,
            scene.WorldId,
            scene.Name,
            scene.Owner,
            Array.AsReadOnly(overlays),
            conflicts,
            Array.AsReadOnly(scene.Dirty.ToArray()),
            scene.Failure);
    }

    private static bool ObserverScopesOverlap(SceneObserverScope first, SceneObserverScope second) =>
        first.IsGlobal || second.IsGlobal || first.Players.Overlaps(second.Players);

    private static bool IsAirMask(SceneOverlayDefinition definition) => definition.Enabled && definition.FillOutsideWithAir;

    private static SceneRenderRequest RenderRequest(
        SceneState scene,
        SceneBounds bounds,
        SceneObserverScope observers,
        bool refreshLoadedSections = false) =>
        new(
            scene.Id,
            scene.WorldId,
            bounds,
            observers.IsGlobal,
            observers.Players) { RefreshLoadedSections = refreshLoadedSections };

    private static void RecordDirty(SceneState scene, SceneBounds bounds)
    {
        if (scene.Dirty.Count == MaximumDirtyRegions)
        {
            scene.Dirty.Dequeue();
        }

        scene.Dirty.Enqueue(bounds);
    }

    private sealed class Lease(SceneRuntime owner, SceneId id) : ISceneLease
    {
        private int disposed;

        public SceneId Id => id;

        public OverlayId AddOverlay(SceneOverlayDefinition definition)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.Add(id, definition);
        }

        public bool UpdateOverlay(OverlayId overlayId, SceneOverlayDefinition definition)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.Update(id, overlayId, definition);
        }

        public bool RemoveOverlay(OverlayId overlayId)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.Remove(id, overlayId);
        }

        public bool BringToFront(OverlayId overlayId)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.InvalidateOverlay(id, overlayId, true);
        }

        public bool Redraw(OverlayId overlayId)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.InvalidateOverlay(id, overlayId, false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.RemoveScene(id);
            }
        }
    }

    private sealed record OverlayState(SceneOverlayDefinition Definition, long Sequence);

    private sealed class SceneState(SceneId id, WorldId worldId, string name, string owner)
    {
        public SceneId Id { get; } = id;
        public WorldId WorldId { get; } = worldId;
        public string Name { get; } = name;
        public string Owner { get; } = owner;
        public Dictionary<OverlayId, OverlayState> Overlays { get; } = [];
        public Queue<SceneBounds> Dirty { get; } = [];
        public string? Failure { get; set; }
    }
}
