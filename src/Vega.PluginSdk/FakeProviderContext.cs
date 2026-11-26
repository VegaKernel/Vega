using Vega.Contracts.Modules;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Plugin-owned, world-local FakeProvider-style tile and visibility capabilities.</summary>
public interface IPluginFakeProvider
{
    bool IsAvailable { get; }
    IPluginTileProvider CreateGlobal(string name, SceneBounds bounds, SceneTileArea tiles, int layer = 0);
    IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
        IEnumerable<SceneSessionObserver> observers, int layer = 0);
    IPluginVisibilityLayer CreateVisibilityLayer(string name, IEnumerable<SceneSessionObserver> observers);
}

/// <summary>A provider lease; disposal restores canonical tiles and is automatic on plugin unload.</summary>
public interface IPluginTileProvider : IDisposable
{
    OverlayId Id { get; }
    /// <summary>Requested provider definition; disconnected observers never match the live runtime.</summary>
    SceneOverlayDefinition Definition { get; }
    void Move(int dx, int dy);
    void Replace(SceneBounds bounds, SceneTileArea tiles);
    void SetLayer(int layer);
    void SetTop();
    void SetEnabled(bool enabled);
    void SetObservers(IEnumerable<SceneSessionObserver> observers);
    void Draw();
}

/// <summary>Scoped entity visibility lease. NPC/projectile IDs remain native slot identities.</summary>
public interface IPluginVisibilityLayer : IDisposable
{
    VisibilityLayerId Id { get; }
    void SetObservers(IEnumerable<SceneSessionObserver> observers);
    bool AssignPlayer(SceneSessionObserver player);
    bool RemovePlayer(SceneSessionObserver player);
    void AssignNpc(int slot);
    void AssignProjectile(int slot);
    bool RemoveNpc(int slot);
    bool RemoveProjectile(int slot);
}

internal static partial class VegaPluginRuntime
{
    // One tracked owner per plugin generation, not one tracked registration per temporary preview.
    private sealed class PluginFakeProvider(IVegaPluginContext context) : IPluginFakeProvider, IDisposable
    {
        private readonly object sync = new();
        private readonly HashSet<OwnedLease> active = [];
        private bool tracked;
        private bool disposed;

        public bool IsAvailable { get { lock (sync) return !disposed && Services.Scenes is not null; } }

        public IPluginTileProvider CreateGlobal(string name, SceneBounds bounds, SceneTileArea tiles, int layer = 0) =>
            Create(name, bounds, tiles, SceneObserverScope.Global, layer);

        public IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
            IEnumerable<SceneSessionObserver> observers, int layer = 0) =>
            Create(name, bounds, tiles, SceneObserverScope.PersonalSessions(observers), layer);

        private IPluginTileProvider Create(string name, SceneBounds bounds, SceneTileArea tiles, SceneObserverScope scope, int layer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(tiles);
            lock (sync)
            {
                EnsureTracked();
                var current = Services;
                var runtime = current.Scenes ?? throw new InvalidOperationException("FakeProvider tile capability is unavailable in this host.");
                ISceneLease lease = runtime.CreateScene(current.WorldId, name, Actor(context));
                try
                {
                    var definition = new SceneOverlayDefinition(default, name, bounds, layer, scope, tiles);
                    var id = lease.AddOverlay(definition);
                    var provider = new TileProvider(this, lease, definition with { Id = id });
                    active.Add(provider);
                    return provider;
                }
                catch { lease.Dispose(); throw; }
            }
        }

        public IPluginVisibilityLayer CreateVisibilityLayer(string name, IEnumerable<SceneSessionObserver> observers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            // Materialize external enumeration before entering the host/scene lock.
            var selected = observers.ToArray();
            lock (sync)
            {
                EnsureTracked();
                var current = Services;
                var runtime = current.Visibility ?? throw new InvalidOperationException("FakeProvider visibility capability is unavailable in this host.");
                var lease = runtime.CreateSessionLayer(current.WorldId, name, Actor(context), selected);
                var layer = new VisibilityLayer(this, lease);
                active.Add(layer);
                return layer;
            }
        }

        private void EnsureTracked()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (tracked) return;
            if (context is not IHotModuleContext owner)
                throw new InvalidOperationException("Plugin host does not expose a tracked hot-module lifetime.");
            // Track before creating any scene or layer. A concurrently disposed scope cannot leak one.
            owner.Track(this);
            tracked = true;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                List<Exception>? failures = null;
                foreach (var lease in active.ToArray())
                {
                    try { lease.Dispose(); }
                    catch (Exception ex) { (failures ??= []).Add(ex); }
                }
                if (failures is not null) throw new AggregateException("FakeProvider cleanup failed.", failures);
            }
        }

        private abstract class OwnedLease(PluginFakeProvider owner, IDisposable lease) : IDisposable
        {
            protected PluginFakeProvider Owner { get; } = owner;
            protected IDisposable Lease { get; } = lease;
            private bool disposed;
            protected T Read<T>(Func<T> action)
            {
                lock (Owner.sync)
                {
                    ObjectDisposedException.ThrowIf(disposed || Owner.disposed, this);
                    return action();
                }
            }
            protected void Mutate(Action action) => Read(() => { action(); return true; });
            public void Dispose()
            {
                lock (Owner.sync)
                {
                    if (disposed) return;
                    disposed = true;
                    Owner.active.Remove(this);
                    Lease.Dispose();
                }
            }
        }

        private sealed class TileProvider(PluginFakeProvider owner, ISceneLease lease, SceneOverlayDefinition initial)
            : OwnedLease(owner, lease), IPluginTileProvider
        {
            private SceneOverlayDefinition definition = initial;
            private ISceneLease Scene => (ISceneLease)Lease;
            public OverlayId Id => definition.Id;
            public SceneOverlayDefinition Definition => Read(() => definition);

            public void Move(int dx, int dy) => Update(current => current with
            {
                Bounds = current.Bounds with { X = checked(current.Bounds.X + dx), Y = checked(current.Bounds.Y + dy) }
            });
            public void Replace(SceneBounds bounds, SceneTileArea tiles) => Update(current => current with { Bounds = bounds, Tiles = tiles });
            public void SetLayer(int layer) => Update(current => current with { Order = layer });
            public void SetEnabled(bool enabled) => Update(current => current with { Enabled = enabled });
            public void SetObservers(IEnumerable<SceneSessionObserver> observers)
            {
                var scope = SceneObserverScope.PersonalSessions(observers);
                Update(current => current.Observers.IsGlobal
                    ? throw new InvalidOperationException("A global provider cannot change into a personal provider.")
                    : current with { Observers = scope });
            }
            public void SetTop() => Mutate(() => Require(Scene.BringToFront(Id)));
            public void Draw() => Mutate(() => Require(Scene.Redraw(Id)));

            private void Update(Func<SceneOverlayDefinition, SceneOverlayDefinition> edit) => Mutate(() =>
            {
                var next = edit(definition);
                if (next == definition) return;
                Require(Scene.UpdateOverlay(Id, next));
                definition = next;
            });
            private static void Require(bool success)
            {
                if (!success) throw new InvalidOperationException("The provider overlay no longer exists.");
            }
        }

        private sealed class VisibilityLayer(PluginFakeProvider owner, IVisibilityLayerLease lease)
            : OwnedLease(owner, lease), IPluginVisibilityLayer
        {
            private IVisibilityLayerLease Layer => (IVisibilityLayerLease)Lease;
            public VisibilityLayerId Id => Layer.Id;
            public void SetObservers(IEnumerable<SceneSessionObserver> observers)
            {
                var selected = observers.ToArray();
                Mutate(() => Layer.SetSessionObservers(selected));
            }
            public bool AssignPlayer(SceneSessionObserver player) => Read(() => Layer.AssignPlayerSession(player));
            public bool RemovePlayer(SceneSessionObserver player) => Read(() => Layer.RemovePlayerSession(player));
            public void AssignNpc(int slot) => Mutate(() => Layer.Assign(new SceneEntity(SceneEntityKind.Npc, slot)));
            public void AssignProjectile(int slot) => Mutate(() => Layer.Assign(new SceneEntity(SceneEntityKind.Projectile, slot)));
            public bool RemoveNpc(int slot) => Read(() => Layer.Remove(new SceneEntity(SceneEntityKind.Npc, slot)));
            public bool RemoveProjectile(int slot) => Read(() => Layer.Remove(new SceneEntity(SceneEntityKind.Projectile, slot)));
        }
    }
}
