using Vega.Contracts.Modules;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Plugin-owned, world-local FakeProvider-style tile and visibility capabilities.</summary>
public interface IPluginFakeProvider
{
    bool IsAvailable { get; }
    bool SupportsVirtualChests => false;
    bool SupportsVirtualChestTransfers => false;
    bool SupportsVirtualSigns => false;
    bool SupportsVirtualSignEdits => false;
    bool SupportsVirtualItemFrames => false;
    IPluginTileProvider CreateGlobal(string name, SceneBounds bounds, SceneTileArea tiles, int layer = 0);
    IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
        IEnumerable<SceneSessionObserver> observers, int layer = 0);
    IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
        IEnumerable<SceneSessionObserver> observers, bool fillOutsideWithAir, int layer = 0) =>
        fillOutsideWithAir ? throw new NotSupportedException("Personal air backgrounds are unavailable.")
            : CreatePersonal(name, bounds, tiles, observers, layer);
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
    void SetFillOutsideWithAir(bool enabled) => throw new NotSupportedException("Personal air backgrounds are unavailable.");
    void SetObservers(IEnumerable<SceneSessionObserver> observers);
    void Draw();
    IPluginVirtualChest CreateVirtualChest(int localX, int localY, string name, IReadOnlyList<ChestItemSnapshot> items) =>
        throw new NotSupportedException("Virtual chests are unavailable.");
    IPluginVirtualSign CreateVirtualSign(int localX, int localY, string text) =>
        throw new NotSupportedException("Virtual signs are unavailable.");
    IPluginVirtualSign CreateVirtualSign(int localX, int localY, string text, bool allowEdits) =>
        allowEdits ? throw new NotSupportedException("Virtual sign edits are unavailable.") : CreateVirtualSign(localX, localY, text);
    IPluginVirtualItemFrame CreateVirtualItemFrame(int localX, int localY, int itemType, int stack, byte prefix = 0) =>
        throw new NotSupportedException("Virtual item frames are unavailable.");
}

/// <summary>Logical container owned by its provider. Explicit transfers use native inventory on the game thread.</summary>
public interface IPluginVirtualChest : IDisposable
{
    VirtualChestId Id { get; }
    SceneVirtualChestDefinition Definition { get; }
    bool TryReplace(long expectedRevision, string name, IReadOnlyList<ChestItemSnapshot> items);
    bool TryTakeOpenRequest(out SceneVirtualChestOpenRequest? request);
    /// <summary>Requires a current interaction and the native inventory revision. Invoke on the game thread (Tasks.Post).</summary>
    SceneVirtualChestTransferResult Transfer(SceneVirtualChestTransferRequest request) => new(VirtualChestTransferStatus.Unavailable);
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
    private sealed partial class PluginFakeProvider(IVegaPluginContext context) : IPluginFakeProvider, IDisposable
    {
        private readonly object sync = new();
        private readonly HashSet<OwnedLease> active = [];
        private bool tracked;
        private bool disposed;

        public bool IsAvailable { get { lock (sync) return !disposed && Services.Scenes is not null; } }
        public bool SupportsVirtualChests { get { lock (sync) return !disposed && Services.Scenes?.SupportsVirtualChests == true; } }
        public bool SupportsVirtualChestTransfers { get { lock (sync) return !disposed && Services.Scenes is IVirtualChestTransferAuthority && Services.Inventory is IVirtualChestInventoryTransfer; } }
        public bool SupportsVirtualSigns { get { lock (sync) return !disposed && Services.Scenes?.SupportsVirtualSigns == true; } }
        public bool SupportsVirtualSignEdits { get { lock (sync) return !disposed && Services.Scenes?.SupportsVirtualSignEdits == true; } }
        public bool SupportsVirtualItemFrames { get { lock (sync) return !disposed && Services.Scenes?.SupportsVirtualItemFrames == true; } }

        public IPluginTileProvider CreateGlobal(string name, SceneBounds bounds, SceneTileArea tiles, int layer = 0) =>
            Create(name, bounds, tiles, SceneObserverScope.Global, layer);

        public IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
            IEnumerable<SceneSessionObserver> observers, int layer = 0) =>
            Create(name, bounds, tiles, SceneObserverScope.PersonalSessions(observers), layer);

        public IPluginTileProvider CreatePersonal(string name, SceneBounds bounds, SceneTileArea tiles,
            IEnumerable<SceneSessionObserver> observers, bool fillOutsideWithAir, int layer = 0) =>
            Create(name, bounds, tiles, SceneObserverScope.PersonalSessions(observers), layer, fillOutsideWithAir);

        private IPluginTileProvider Create(string name, SceneBounds bounds, SceneTileArea tiles, SceneObserverScope scope, int layer,
            bool fillOutsideWithAir = false)
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
                    var definition = new SceneOverlayDefinition(default, name, bounds, layer, scope, tiles,
                        FillOutsideWithAir: fillOutsideWithAir);
                    var id = lease.AddOverlay(definition);
                    var provider = new TileProvider(this, lease, runtime, definition with { Id = id });
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
                    AssertAlive();
                    return action();
                }
            }
            protected void AssertAlive() => ObjectDisposedException.ThrowIf(disposed || Owner.disposed, this);
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

        private sealed partial class TileProvider(PluginFakeProvider owner, ISceneLease lease, ISceneRuntime runtime, SceneOverlayDefinition initial)
            : OwnedLease(owner, lease), IPluginTileProvider
        {
            private SceneOverlayDefinition definition = initial;
            private readonly ISceneRuntime sceneRuntime = runtime;
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
            public void SetFillOutsideWithAir(bool enabled) => Update(current => current with { FillOutsideWithAir = enabled });
            public void SetObservers(IEnumerable<SceneSessionObserver> observers)
            {
                var scope = SceneObserverScope.PersonalSessions(observers);
                Update(current => current.Observers.IsGlobal
                    ? throw new InvalidOperationException("A global provider cannot change into a personal provider.")
                    : current with { Observers = scope });
            }
            public void SetTop() => Mutate(() => Require(Scene.BringToFront(Id)));
            public void Draw() => Mutate(() => Require(Scene.Redraw(Id)));

            public IPluginVirtualChest CreateVirtualChest(int localX, int localY, string name, IReadOnlyList<ChestItemSnapshot> items) => Read(() =>
            {
                if (!sceneRuntime.SupportsVirtualChests) throw new NotSupportedException("Virtual chests are unavailable in this host.");
                var chest = new SceneVirtualChestDefinition(VirtualChestId.New(), localX, localY, name, 1, items);
                SetChests([.. definition.VirtualChests, chest]);
                return new VirtualChest(this, chest.Id);
            });

            private void SetChests(SceneVirtualChestDefinition[] chests)
            {
                var next = definition with { VirtualChests = chests.Length == 0 ? Array.Empty<SceneVirtualChestDefinition>() : Array.AsReadOnly(chests) };
                Require(Scene.UpdateOverlay(Id, next));
                definition = next;
            }

            private SceneVirtualChestDefinition ReadChest(VirtualChestId id) => Read(() =>
                definition.VirtualChests.FirstOrDefault(chest => chest.Id == id) ??
                    throw new ObjectDisposedException($"Virtual chest {id.Value}"));

            private bool ReplaceChest(VirtualChestId id, long expectedRevision, string name, IReadOnlyList<ChestItemSnapshot> items) => Read(() =>
            {
                var current = definition.VirtualChests.FirstOrDefault(chest => chest.Id == id);
                if (current is null || current.Revision != expectedRevision) return false;
                var next = new SceneVirtualChestDefinition(id, current.LocalX, current.LocalY, name, checked(current.Revision + 1), items);
                SetChests(definition.VirtualChests.Select(chest => chest.Id == id ? next : chest).ToArray());
                return true;
            });

            private void RemoveChest(VirtualChestId id) => Mutate(() =>
            {
                if (definition.VirtualChests.Any(chest => chest.Id == id))
                    SetChests(definition.VirtualChests.Where(chest => chest.Id != id).ToArray());
            });

            private (bool Found, SceneVirtualChestOpenRequest? Request) TakeChestOpen(VirtualChestId id)
            {
                lock (Owner.sync)
                {
                    AssertAlive();
                    var chests = definition.VirtualChests;
                    for (int i = 0; i < chests.Count; i++)
                        if (chests[i].Id == id)
                        {
                            bool found = sceneRuntime.TryTakeVirtualChestOpen(Scene.Id, Id, id, out var request);
                            return (found, request);
                        }
                    return (false, null);
                }
            }

            private sealed class VirtualChest(TileProvider provider, VirtualChestId id) : IPluginVirtualChest
            {
                private int disposed;
                public VirtualChestId Id => id;
                public SceneVirtualChestDefinition Definition { get { AssertOpen(); return provider.ReadChest(id); } }
                public bool TryReplace(long expectedRevision, string name, IReadOnlyList<ChestItemSnapshot> items)
                {
                    AssertOpen();
                    return provider.ReplaceChest(id, expectedRevision, name, items);
                }
                public bool TryTakeOpenRequest(out SceneVirtualChestOpenRequest? request)
                {
                    AssertOpen();
                    var result = provider.TakeChestOpen(id);
                    request = result.Request;
                    return result.Found;
                }
                public SceneVirtualChestTransferResult Transfer(SceneVirtualChestTransferRequest request)
                {
                    AssertOpen();
                    return provider.TransferChest(id, request);
                }
                public void Dispose()
                {
                    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                    try { provider.RemoveChest(id); }
                    catch (ObjectDisposedException) { /* The owning provider was already removed. */ }
                }
                private void AssertOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            }

            private SceneVirtualChestTransferResult TransferChest(VirtualChestId id, SceneVirtualChestTransferRequest request) => Read(() =>
            {
                if (request.Open.Chest.SceneId != Scene.Id || request.Open.Chest.OverlayId != Id ||
                    request.Open.Chest.Definition?.Id != id || !definition.VirtualChests.Any(chest => chest.Id == id))
                    return new SceneVirtualChestTransferResult(VirtualChestTransferStatus.StaleInteraction);
                if (Services.Inventory is not IVirtualChestInventoryTransfer transfer)
                    return new SceneVirtualChestTransferResult(VirtualChestTransferStatus.Unavailable);
                var result = transfer.TransferVirtualChest(sceneRuntime, request);
                if (result.Status == VirtualChestTransferStatus.Applied && result.Chest is { } updated)
                {
                    // The runtime already published this revision as part of the native transaction.
                    var chests = definition.VirtualChests.Select(chest => chest.Id == id ? updated : chest).ToArray();
                    definition = definition with { VirtualChests = Array.AsReadOnly(chests) };
                }
                return result;
            });

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
