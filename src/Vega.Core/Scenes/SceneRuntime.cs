using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed class SceneRuntime(ISceneRenderSink? sink = null) : ISceneRuntime
{
    private const int MaximumDirtyRegions = 128;
    private readonly object sync = new();
    private readonly Dictionary<SceneId, SceneState> scenes = [];
    private long nextSequence;

    public bool HasActiveOverlays
    {
        get
        {
            lock (sync)
            {
                foreach (SceneState scene in scenes.Values)
                {
                    foreach (OverlayState overlay in scene.Overlays.Values)
                    {
                        if (overlay.Definition.Enabled)
                        {
                            return true;
                        }
                    }
                }

                return false;
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
        lock (sync)
        {
            OverlayState? selected = null;
            foreach (SceneState scene in scenes.Values)
            {
                if (scene.WorldId != worldId)
                {
                    continue;
                }

                foreach (OverlayState candidate in scene.Overlays.Values)
                {
                    SceneOverlayDefinition definition = candidate.Definition;
                    if (!definition.Enabled ||
                        !definition.Observers.Contains(observer) ||
                        !definition.Bounds.Contains(tileX, tileY) ||
                        selected is not null &&
                        (definition.Order < selected.Definition.Order ||
                         definition.Order == selected.Definition.Order && candidate.Sequence < selected.Sequence))
                    {
                        continue;
                    }

                    selected = candidate;
                }
            }

            if (selected is null)
            {
                tile = default;
                overlayId = default;
                return false;
            }

            SceneBounds bounds = selected.Definition.Bounds;
            tile = selected.Definition.Tiles[tileX - bounds.X, tileY - bounds.Y];
            overlayId = selected.Definition.Id;
            return true;
        }
    }

    private OverlayId Add(SceneId sceneId, SceneOverlayDefinition definition)
    {
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

            SceneOverlayDefinition stored = definition with { Id = id };
            scene.Overlays.Add(id, new OverlayState(stored, ++nextSequence));
            RecordDirty(scene, stored.Bounds);
            request = RenderRequest(scene, stored.Bounds, stored.Observers);
            definition = stored;
        }

        sink?.Invalidate(request);
        return definition.Id;
    }

    private bool Update(SceneId sceneId, OverlayId overlayId, SceneOverlayDefinition definition)
    {
        Validate(definition);
        SceneRenderRequest[] requests;
        lock (sync)
        {
            SceneState scene = Get(sceneId);
            if (!scene.Overlays.TryGetValue(overlayId, out OverlayState? current))
            {
                return false;
            }

            SceneOverlayDefinition stored = definition with { Id = overlayId };
            scene.Overlays[overlayId] = new OverlayState(stored, current.Sequence);
            RecordDirty(scene, current.Definition.Bounds);
            RecordDirty(scene, stored.Bounds);
            requests =
            [
                RenderRequest(scene, current.Definition.Bounds, current.Definition.Observers),
                RenderRequest(scene, stored.Bounds, stored.Observers)
            ];
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

            RecordDirty(scene, removed.Definition.Bounds);
            request = RenderRequest(scene, removed.Definition.Bounds, removed.Definition.Observers);
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

            requests = scene.Overlays.Values
                .Select(overlay => RenderRequest(scene, overlay.Definition.Bounds, overlay.Definition.Observers))
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

    private static SceneRenderRequest RenderRequest(
        SceneState scene,
        SceneBounds bounds,
        SceneObserverScope observers) =>
        new(
            scene.Id,
            scene.WorldId,
            bounds,
            observers.IsGlobal,
            observers.IsGlobal ? new HashSet<PlayerId>() : new HashSet<PlayerId>(observers.Players));

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
