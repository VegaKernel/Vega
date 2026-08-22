using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

public readonly record struct SceneId(Guid Value)
{
    public static SceneId New() => new(Guid.NewGuid());
}

public readonly record struct OverlayId(Guid Value)
{
    public static OverlayId New() => new(Guid.NewGuid());
}

public readonly record struct SceneBounds(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public bool Intersects(SceneBounds other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
}

[Flags]
public enum SceneTileFlags : ushort
{
    None = 0,
    Active = 1,
    WireRed = 1 << 1,
    WireBlue = 1 << 2,
    WireGreen = 1 << 3,
    WireYellow = 1 << 4,
    HalfBlock = 1 << 5,
    Actuator = 1 << 6,
    Inactive = 1 << 7,
    FullbrightBlock = 1 << 8,
    FullbrightWall = 1 << 9,
    InvisibleBlock = 1 << 10,
    InvisibleWall = 1 << 11
}

public readonly record struct SceneTile(
    ushort TileType,
    ushort WallType = 0,
    short FrameX = 0,
    short FrameY = 0,
    byte LiquidAmount = 0,
    byte LiquidKind = 0,
    byte TileColor = 0,
    byte WallColor = 0,
    byte Slope = 0,
    SceneTileFlags Flags = SceneTileFlags.None)
{
    public static SceneTile Air { get; } = new(0);
}

public sealed record SceneTileArea
{
    public SceneTileArea(int width, int height, IReadOnlyList<SceneTile> tiles)
    {
        if (width <= 0 || height <= 0 || tiles.Count != checked(width * height))
        {
            throw new ArgumentException("Tile area dimensions must match its tile count.", nameof(tiles));
        }

        Width = width;
        Height = height;
        Tiles = Array.AsReadOnly(tiles.ToArray());
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<SceneTile> Tiles { get; }

    public SceneTile this[int x, int y] => Tiles[checked(y * Width + x)];
}

public sealed record SceneObserverScope(bool IsGlobal, IReadOnlySet<PlayerId> Players)
{
    public static SceneObserverScope Global { get; } = new(true, new HashSet<PlayerId>());

    public static SceneObserverScope Personal(IEnumerable<PlayerId> players) =>
        new(false, new HashSet<PlayerId>(players));

    public bool Contains(PlayerId playerId) => IsGlobal || Players.Contains(playerId);
}

public sealed record SceneOverlayDefinition(
    OverlayId Id,
    string Name,
    SceneBounds Bounds,
    int Order,
    SceneObserverScope Observers,
    SceneTileArea Tiles,
    bool Enabled = true,
    bool FillOutsideWithAir = false);

public sealed record SceneSnapshot(
    SceneId Id,
    WorldId WorldId,
    string Name,
    string Owner,
    IReadOnlyList<SceneOverlayDefinition> Overlays,
    int ConflictCount,
    IReadOnlyList<SceneBounds> DirtyRegions,
    string? Failure = null);

public sealed record SceneRenderRequest(
    SceneId SceneId,
    WorldId WorldId,
    SceneBounds DirtyRegion,
    bool IsGlobal,
    IReadOnlySet<PlayerId> Observers);

public interface ISceneRenderSink
{
    void Invalidate(SceneRenderRequest request);
}

public interface ISceneLease : IDisposable
{
    SceneId Id { get; }

    OverlayId AddOverlay(SceneOverlayDefinition definition);

    bool UpdateOverlay(OverlayId overlayId, SceneOverlayDefinition definition);

    bool RemoveOverlay(OverlayId overlayId);
}

public interface ISceneRuntime
{
    bool HasActiveOverlays { get; }

    ISceneLease CreateScene(WorldId worldId, string name, string owner);

    IReadOnlyList<SceneSnapshot> SnapshotScenes();

    void ReportFailure(SceneId sceneId, string? failure);

    bool TryResolveTile(
        WorldId worldId,
        PlayerId observer,
        int tileX,
        int tileY,
        out SceneTile tile,
        out OverlayId overlayId);
}

public enum SceneEntityKind
{
    Player,
    Npc,
    Projectile
}

public readonly record struct VisibilityLayerId(Guid Value)
{
    public static VisibilityLayerId New() => new(Guid.NewGuid());
}

public readonly record struct SceneEntity(SceneEntityKind Kind, int Id);

public sealed record VisibilityLayerSnapshot(
    VisibilityLayerId Id,
    WorldId WorldId,
    string Name,
    string Owner,
    IReadOnlySet<PlayerId> Observers,
    IReadOnlySet<SceneEntity> Entities);

public interface IVisibilityLayerLease : IDisposable
{
    VisibilityLayerId Id { get; }

    void SetObservers(IEnumerable<PlayerId> observers);

    void Assign(SceneEntity entity);

    bool Remove(SceneEntity entity);
}

public interface ISceneVisibilityRuntime
{
    bool HasAssignments { get; }

    IVisibilityLayerLease CreateLayer(
        WorldId worldId,
        string name,
        string owner,
        IEnumerable<PlayerId> observers);

    bool CanObserve(WorldId worldId, PlayerId observer, SceneEntity entity);

    IReadOnlyList<VisibilityLayerSnapshot> SnapshotLayers();
}
