using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Scenes;

/// <summary>Named global and personal tile providers backed by Vega scene leases, not Main.tile.</summary>
public sealed class FakeProviderService(ISceneRuntime scenes, WorldId? worldId = null) : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<string, Provider> providers = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public bool Create(string name, SceneBounds bounds, SceneTileArea tiles,
        IEnumerable<PlayerId>? observers = null, int layer = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (providers.ContainsKey(name)) return false;
            var scope = observers is null ? SceneObserverScope.Global : SceneObserverScope.Personal(observers);
            var definition = new SceneOverlayDefinition(default, name, bounds, layer, scope, tiles);
            ISceneLease scene = scenes.CreateScene(worldId ?? WorldId.Primary, $"fake:{name}", $"FakeProvider:{name}");
            try
            {
                OverlayId id = scene.AddOverlay(definition);
                providers.Add(name, new Provider(scene, definition with { Id = id }));
                return true;
            }
            catch
            {
                scene.Dispose();
                throw;
            }
        }
    }

    public IReadOnlyList<SceneOverlayDefinition> Snapshot(bool personal)
    {
        lock (sync)
            return providers.Values.Select(static provider => provider.Definition)
                .Where(definition => definition.Observers.IsGlobal != personal)
                .OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryGet(string name, bool personal, out SceneOverlayDefinition? definition)
    {
        lock (sync)
        {
            if (providers.TryGetValue(name, out var provider) && provider.Definition.Observers.IsGlobal != personal)
            {
                definition = provider.Definition;
                return true;
            }
            definition = null;
            return false;
        }
    }

    public bool Move(string name, bool personal, int dx, int dy) => Update(name, personal, definition => definition with
    {
        Bounds = definition.Bounds with { X = checked(definition.Bounds.X + dx), Y = checked(definition.Bounds.Y + dy) }
    });

    public bool ReplaceTiles(string name, bool personal, SceneBounds bounds, SceneTileArea tiles) =>
        Update(name, personal, definition => definition with { Bounds = bounds, Tiles = tiles });

    public bool SetLayer(string name, bool personal, int layer) =>
        Update(name, personal, definition => definition with { Order = layer });

    public bool SetEnabled(string name, bool personal, bool enabled) =>
        Update(name, personal, definition => definition with { Enabled = enabled });

    public bool SetObservers(string name, IEnumerable<PlayerId> observers) =>
        Update(name, true, definition => definition with { Observers = SceneObserverScope.Personal(observers) });

    public bool Remove(string name, bool personal)
    {
        lock (sync)
        {
            if (!providers.TryGetValue(name, out var provider) || provider.Definition.Observers.IsGlobal == personal) return false;
            providers.Remove(name);
            provider.Scene.Dispose();
            return true;
        }
    }

    internal void RemoveObserver(PlayerId observer)
    {
        lock (sync)
        {
            foreach (var provider in providers.Values.Where(p => !p.Definition.Observers.IsGlobal && p.Definition.Observers.Contains(observer)).ToArray())
            {
                var remaining = provider.Definition.Observers.Players.Where(player => player != observer).ToArray();
                if (remaining.Length == 0) Remove(provider.Definition.Name, true);
                else SetObservers(provider.Definition.Name, remaining);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            foreach (var provider in providers.Values) provider.Scene.Dispose();
            providers.Clear();
        }
    }

    private bool Update(string name, bool personal, Func<SceneOverlayDefinition, SceneOverlayDefinition> update)
    {
        lock (sync)
        {
            if (!providers.TryGetValue(name, out var provider) || provider.Definition.Observers.IsGlobal == personal) return false;
            var next = update(provider.Definition);
            if (next == provider.Definition) return true;
            if (!provider.Scene.UpdateOverlay(next.Id, next)) return false;
            providers[name] = provider with { Definition = next };
            return true;
        }
    }

    private sealed record Provider(ISceneLease Scene, SceneOverlayDefinition Definition);
}
