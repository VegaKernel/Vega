using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed class SceneVisibilityRuntime : ISceneVisibilityRuntime
{
    private readonly object sync = new();
    private readonly Dictionary<VisibilityLayerId, LayerState> layers = [];
    private readonly Dictionary<(WorldId WorldId, SceneEntity Entity), VisibilityLayerId> assignments = [];

    public bool HasAssignments
    {
        get
        {
            lock (sync)
            {
                return assignments.Count > 0;
            }
        }
    }

    public IVisibilityLayerLease CreateLayer(
        WorldId worldId,
        string name,
        string owner,
        IEnumerable<PlayerId> observers)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(owner))
        {
            throw new ArgumentException("Visibility layer name and owner are required.");
        }

        var state = new LayerState(
            VisibilityLayerId.New(),
            worldId,
            name.Trim(),
            owner.Trim(),
            new HashSet<PlayerId>(observers));
        lock (sync)
        {
            layers.Add(state.Id, state);
        }

        return new Lease(this, state.Id);
    }

    public bool CanObserve(WorldId worldId, PlayerId observer, SceneEntity entity)
    {
        lock (sync)
        {
            return !assignments.TryGetValue((worldId, entity), out VisibilityLayerId layerId) ||
                !layers.TryGetValue(layerId, out LayerState? layer) ||
                layer.Observers.Contains(observer);
        }
    }

    public IReadOnlyList<VisibilityLayerSnapshot> SnapshotLayers()
    {
        lock (sync)
        {
            return Array.AsReadOnly(layers.Values
                .OrderBy(static layer => layer.WorldId.Value, StringComparer.Ordinal)
                .ThenBy(static layer => layer.Name, StringComparer.Ordinal)
                .Select(static layer => new VisibilityLayerSnapshot(
                    layer.Id,
                    layer.WorldId,
                    layer.Name,
                    layer.Owner,
                    new HashSet<PlayerId>(layer.Observers),
                    new HashSet<SceneEntity>(layer.Entities)))
                .ToArray());
        }
    }

    private void SetObservers(VisibilityLayerId id, IEnumerable<PlayerId> observers)
    {
        lock (sync)
        {
            Get(id).Observers = new HashSet<PlayerId>(observers);
        }
    }

    private void Assign(VisibilityLayerId id, SceneEntity entity)
    {
        if (entity.Id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(entity));
        }

        lock (sync)
        {
            LayerState layer = Get(id);
            var key = (layer.WorldId, entity);
            if (assignments.TryGetValue(key, out VisibilityLayerId previous) &&
                layers.TryGetValue(previous, out LayerState? previousLayer))
            {
                previousLayer.Entities.Remove(entity);
            }

            assignments[key] = id;
            layer.Entities.Add(entity);
        }
    }

    private bool Remove(VisibilityLayerId id, SceneEntity entity)
    {
        lock (sync)
        {
            LayerState layer = Get(id);
            assignments.Remove((layer.WorldId, entity));
            return layer.Entities.Remove(entity);
        }
    }

    private void RemoveLayer(VisibilityLayerId id)
    {
        lock (sync)
        {
            if (!layers.Remove(id, out LayerState? layer))
            {
                return;
            }

            foreach (SceneEntity entity in layer.Entities)
            {
                assignments.Remove((layer.WorldId, entity));
            }
        }
    }

    private LayerState Get(VisibilityLayerId id) => layers.TryGetValue(id, out LayerState? layer)
        ? layer
        : throw new ObjectDisposedException($"Visibility layer {id.Value}");

    private sealed class Lease(SceneVisibilityRuntime owner, VisibilityLayerId id) : IVisibilityLayerLease
    {
        private int disposed;

        public VisibilityLayerId Id => id;

        public void SetObservers(IEnumerable<PlayerId> observers)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            owner.SetObservers(id, observers);
        }

        public void Assign(SceneEntity entity)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            owner.Assign(id, entity);
        }

        public bool Remove(SceneEntity entity)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            return owner.Remove(id, entity);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.RemoveLayer(id);
            }
        }
    }

    private sealed class LayerState(
        VisibilityLayerId id,
        WorldId worldId,
        string name,
        string owner,
        HashSet<PlayerId> observers)
    {
        public VisibilityLayerId Id { get; } = id;
        public WorldId WorldId { get; } = worldId;
        public string Name { get; } = name;
        public string Owner { get; } = owner;
        public HashSet<PlayerId> Observers { get; set; } = observers;
        public HashSet<SceneEntity> Entities { get; } = [];
    }
}
