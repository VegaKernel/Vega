using System.Reflection;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Scenes;

public sealed class SceneConsumersModule : IVegaModule
{
    private readonly IPlayerSessionLifecycle? sessions;
    private bool started;

    public SceneConsumersModule(
        ISceneRuntime scenes,
        ISceneVisibilityRuntime visibility,
        IPlayerSessionLifecycle? sessions = null)
    {
        this.sessions = sessions;
        WorldEdit = new WorldEditPreviewService(scenes);
        RegionVision = new RegionVisionService(scenes);
        LobbyVisuals = new LobbyVisualService(scenes);
        Clustering = new FakeProviderClusteringService(visibility);
        LobbyComposition = new LobbySceneCompositionService(LobbyVisuals, Clustering);
    }

    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Modules.Scenes",
        typeof(SceneConsumersModule).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ["Vega.Contracts"]);

    public WorldEditPreviewService WorldEdit { get; }

    public RegionVisionService RegionVision { get; }

    public LobbyVisualService LobbyVisuals { get; }

    public FakeProviderClusteringService Clustering { get; }

    public LobbySceneCompositionService LobbyComposition { get; }

    public void Start()
    {
        if (!started && sessions is not null)
        {
            sessions.Closed += OnSessionClosed;
            started = true;
        }
    }

    public void Dispose()
    {
        if (started && sessions is not null)
        {
            sessions.Closed -= OnSessionClosed;
            started = false;
        }

        LobbyComposition.Dispose();
        Clustering.Dispose();
        LobbyVisuals.Dispose();
        RegionVision.Dispose();
        WorldEdit.Dispose();
    }

    private void OnSessionClosed(PlayerSessionClosed closed)
    {
        WorldEdit.Clear(closed.PlayerId);
        RegionVision.Clear(closed.PlayerId);
        LobbyComposition.RemoveObserver(closed.PlayerId);
    }
}

public sealed class FakeProviderClusteringService(ISceneVisibilityRuntime visibility) : IDisposable
{
    private readonly Dictionary<string, IVisibilityLayerLease> layers = new(StringComparer.OrdinalIgnoreCase);

    public IVisibilityLayerLease CreateOrReplace(string name, IEnumerable<PlayerId> observers) =>
        CreateOrReplace(name, observers, []);

    public IVisibilityLayerLease CreateOrReplace(
        string name,
        IEnumerable<PlayerId> observers,
        IEnumerable<SceneEntity> entities)
    {
        Remove(name);
        IVisibilityLayerLease layer = visibility.CreateLayer(
            WorldId.Primary,
            name,
            $"FakeProviderClustering:{name}",
            observers);
        foreach (SceneEntity entity in entities)
        {
            layer.Assign(entity);
        }

        layers[name] = layer;
        return layer;
    }

    public bool SetObservers(string name, IEnumerable<PlayerId> observers)
    {
        if (!layers.TryGetValue(name, out IVisibilityLayerLease? layer))
        {
            return false;
        }

        layer.SetObservers(observers);
        return true;
    }

    public bool Remove(string name)
    {
        if (!layers.Remove(name, out IVisibilityLayerLease? layer))
        {
            return false;
        }

        layer.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (IVisibilityLayerLease layer in layers.Values)
        {
            layer.Dispose();
        }

        layers.Clear();
    }
}

public sealed class WorldEditPreviewService(ISceneRuntime scenes) : IDisposable
{
    private readonly Dictionary<PlayerId, Preview> previews = [];

    public void Show(
        PlayerId observer,
        string sessionName,
        SceneBounds bounds,
        SceneTileArea clipboard,
        SceneTileArea? mask = null)
    {
        Clear(observer);
        ISceneLease scene = scenes.CreateScene(
            WorldId.Primary,
            $"worldedit:{sessionName}",
            $"WorldEdit:{observer.Slot}:{sessionName}");
        SceneObserverScope scope = SceneObserverScope.Personal([observer]);
        OverlayId clipboardId = scene.AddOverlay(new SceneOverlayDefinition(
            default,
            "clipboard",
            bounds,
            100,
            scope,
            clipboard));
        OverlayId? maskId = mask is null
            ? null
            : scene.AddOverlay(new SceneOverlayDefinition(
                default,
                "mask",
                bounds,
                110,
                scope,
                mask));
        previews[observer] = new Preview(scene, clipboardId, maskId);
    }

    public bool Move(
        PlayerId observer,
        SceneBounds bounds,
        SceneTileArea clipboard,
        SceneTileArea? mask = null)
    {
        if (!previews.TryGetValue(observer, out Preview? preview))
        {
            return false;
        }

        SceneObserverScope scope = SceneObserverScope.Personal([observer]);
        bool updated = preview.Scene.UpdateOverlay(preview.Clipboard, new SceneOverlayDefinition(
            preview.Clipboard,
            "clipboard",
            bounds,
            100,
            scope,
            clipboard));
        if (preview.Mask is OverlayId maskId && mask is not null)
        {
            updated &= preview.Scene.UpdateOverlay(maskId, new SceneOverlayDefinition(
                maskId,
                "mask",
                bounds,
                110,
                scope,
                mask));
        }
        else if (preview.Mask is OverlayId removedMask && mask is null)
        {
            updated &= preview.Scene.RemoveOverlay(removedMask);
            previews[observer] = preview with { Mask = null };
        }
        else if (preview.Mask is null && mask is not null)
        {
            OverlayId addedMask = preview.Scene.AddOverlay(new SceneOverlayDefinition(
                default,
                "mask",
                bounds,
                110,
                scope,
                mask));
            previews[observer] = preview with { Mask = addedMask };
        }

        return updated;
    }

    public bool Clear(PlayerId observer)
    {
        if (!previews.Remove(observer, out Preview? preview))
        {
            return false;
        }

        preview.Scene.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (Preview preview in previews.Values)
        {
            preview.Scene.Dispose();
        }

        previews.Clear();
    }

    private sealed record Preview(ISceneLease Scene, OverlayId Clipboard, OverlayId? Mask);
}

public sealed class RegionVisionService(ISceneRuntime scenes) : IDisposable
{
    private readonly Dictionary<PlayerId, ISceneLease> borders = [];

    public void Show(PlayerId observer, string regionName, SceneBounds bounds, ushort tileType = 1)
    {
        Clear(observer);
        ISceneLease scene = scenes.CreateScene(
            WorldId.Primary,
            $"region:{regionName}",
            $"Region-Vision:{observer.Slot}:{regionName}");
        scene.AddOverlay(new SceneOverlayDefinition(
            default,
            "border",
            bounds,
            200,
            SceneObserverScope.Personal([observer]),
            Border(bounds.Width, bounds.Height, tileType)));
        borders[observer] = scene;
    }

    public bool Clear(PlayerId observer)
    {
        if (!borders.Remove(observer, out ISceneLease? scene))
        {
            return false;
        }

        scene.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (ISceneLease scene in borders.Values)
        {
            scene.Dispose();
        }

        borders.Clear();
    }

    private static SceneTileArea Border(int width, int height, ushort tileType)
    {
        SceneTile[] tiles = new SceneTile[checked(width * height)];
        var border = new SceneTile(tileType, Flags: SceneTileFlags.Active);
        for (var x = 0; x < width; x++)
        {
            tiles[x] = border;
            tiles[(height - 1) * width + x] = border;
        }

        for (var y = 0; y < height; y++)
        {
            tiles[y * width] = border;
            tiles[y * width + width - 1] = border;
        }

        return new SceneTileArea(width, height, tiles);
    }
}

public sealed class LobbyVisualService(ISceneRuntime scenes) : IDisposable
{
    private readonly Dictionary<string, Visual> visuals = new(StringComparer.OrdinalIgnoreCase);

    public void Show(
        string name,
        SceneBounds bounds,
        SceneTileArea content,
        IReadOnlyCollection<PlayerId>? observers = null,
        int order = 300)
    {
        Remove(name);
        ISceneLease scene = scenes.CreateScene(WorldId.Primary, $"lobby:{name}", $"LobbyVisuals:{name}");
        OverlayId overlay = scene.AddOverlay(new SceneOverlayDefinition(
            default,
            name,
            bounds,
            order,
            observers is null ? SceneObserverScope.Global : SceneObserverScope.Personal(observers),
            content));
        visuals[name] = new Visual(scene, overlay, bounds, content, order);
    }

    public bool Update(
        string name,
        SceneBounds bounds,
        SceneTileArea content,
        IReadOnlyCollection<PlayerId>? observers = null,
        int order = 300)
    {
        if (!visuals.TryGetValue(name, out Visual? visual))
        {
            return false;
        }

        bool updated = visual.Scene.UpdateOverlay(visual.Overlay, new SceneOverlayDefinition(
            visual.Overlay,
            name,
            bounds,
            order,
            observers is null ? SceneObserverScope.Global : SceneObserverScope.Personal(observers),
            content));
        if (updated)
        {
            visuals[name] = visual with { Bounds = bounds, Content = content, Order = order };
        }

        return updated;
    }

    public bool SetObservers(string name, IReadOnlyCollection<PlayerId> observers)
    {
        if (!visuals.TryGetValue(name, out Visual? visual))
        {
            return false;
        }

        return visual.Scene.UpdateOverlay(visual.Overlay, new SceneOverlayDefinition(
            visual.Overlay,
            name,
            visual.Bounds,
            visual.Order,
            SceneObserverScope.Personal(observers),
            visual.Content));
    }

    public void HidePortal(
        string name,
        SceneBounds bounds,
        IReadOnlyCollection<PlayerId>? observers = null,
        int order = 350)
    {
        Show(
            name,
            bounds,
            new SceneTileArea(
                bounds.Width,
                bounds.Height,
                Enumerable.Repeat(SceneTile.Air, checked(bounds.Width * bounds.Height)).ToArray()),
            observers,
            order);
    }

    public bool Remove(string name)
    {
        if (!visuals.Remove(name, out Visual? visual))
        {
            return false;
        }

        visual.Scene.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (Visual visual in visuals.Values)
        {
            visual.Scene.Dispose();
        }

        visuals.Clear();
    }

    private sealed record Visual(
        ISceneLease Scene,
        OverlayId Overlay,
        SceneBounds Bounds,
        SceneTileArea Content,
        int Order);
}

public sealed class LobbySceneCompositionService(
    LobbyVisualService visuals,
    FakeProviderClusteringService clustering) : IDisposable
{
    private readonly Dictionary<string, HashSet<PlayerId>> observers = new(StringComparer.OrdinalIgnoreCase);

    public void ShowFrame(
        string composition,
        SceneBounds bounds,
        SceneTileArea frame,
        IEnumerable<PlayerId> visibleTo)
    {
        HashSet<PlayerId> scope = visibleTo.ToHashSet();
        observers[composition] = scope;
        string visualName = VisualName(composition, "frame");
        if (!visuals.Update(visualName, bounds, frame, scope))
        {
            visuals.Show(visualName, bounds, frame, scope);
        }
    }

    public void HidePortal(
        string composition,
        SceneBounds bounds,
        IEnumerable<PlayerId> hiddenFor)
    {
        HashSet<PlayerId> scope = hiddenFor.ToHashSet();
        observers[composition] = scope;
        visuals.HidePortal(VisualName(composition, "portal"), bounds, scope);
    }

    public void SetCluster(
        string composition,
        IEnumerable<PlayerId> visibleTo,
        IEnumerable<SceneEntity> entities)
    {
        HashSet<PlayerId> scope = visibleTo.ToHashSet();
        observers[composition] = scope;
        clustering.CreateOrReplace(ClusterName(composition), scope, entities);
    }

    public void RemoveObserver(PlayerId playerId)
    {
        foreach ((string name, HashSet<PlayerId> scope) in observers.ToArray())
        {
            if (!scope.Remove(playerId))
            {
                continue;
            }

            visuals.SetObservers(VisualName(name, "frame"), scope);
            visuals.SetObservers(VisualName(name, "portal"), scope);
            clustering.SetObservers(ClusterName(name), scope);
        }
    }

    public void Remove(string composition)
    {
        observers.Remove(composition);
        visuals.Remove(VisualName(composition, "frame"));
        visuals.Remove(VisualName(composition, "portal"));
        clustering.Remove(ClusterName(composition));
    }

    public void Dispose()
    {
        foreach (string composition in observers.Keys.ToArray())
        {
            Remove(composition);
        }
    }

    private static string VisualName(string composition, string role) => $"{composition}:{role}";

    private static string ClusterName(string composition) => $"{composition}:cluster";
}
