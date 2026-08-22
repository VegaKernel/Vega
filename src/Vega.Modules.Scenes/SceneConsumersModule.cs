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

        Clustering.Dispose();
        LobbyVisuals.Dispose();
        RegionVision.Dispose();
        WorldEdit.Dispose();
    }

    private void OnSessionClosed(PlayerSessionClosed closed)
    {
        WorldEdit.Clear(closed.PlayerId);
        RegionVision.Clear(closed.PlayerId);
    }
}

public sealed class FakeProviderClusteringService(ISceneVisibilityRuntime visibility) : IDisposable
{
    private readonly Dictionary<string, IVisibilityLayerLease> layers = new(StringComparer.OrdinalIgnoreCase);

    public IVisibilityLayerLease CreateOrReplace(string name, IEnumerable<PlayerId> observers)
    {
        Remove(name);
        IVisibilityLayerLease layer = visibility.CreateLayer(
            WorldId.Primary,
            name,
            $"FakeProviderClustering:{name}",
            observers);
        layers[name] = layer;
        return layer;
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
    private readonly Dictionary<string, ISceneLease> visuals = new(StringComparer.OrdinalIgnoreCase);

    public void Show(
        string name,
        SceneBounds bounds,
        SceneTileArea content,
        IReadOnlyCollection<PlayerId>? observers = null,
        int order = 300)
    {
        Remove(name);
        ISceneLease scene = scenes.CreateScene(WorldId.Primary, $"lobby:{name}", $"LobbyVisuals:{name}");
        scene.AddOverlay(new SceneOverlayDefinition(
            default,
            name,
            bounds,
            order,
            observers is null ? SceneObserverScope.Global : SceneObserverScope.Personal(observers),
            content));
        visuals[name] = scene;
    }

    public bool Remove(string name)
    {
        if (!visuals.Remove(name, out ISceneLease? scene))
        {
            return false;
        }

        scene.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (ISceneLease scene in visuals.Values)
        {
            scene.Dispose();
        }

        visuals.Clear();
    }
}
