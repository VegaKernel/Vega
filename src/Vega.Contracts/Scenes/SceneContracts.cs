using System.Buffers;
using System.Collections.Frozen;
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

    public bool TryIntersect(SceneBounds other, out SceneBounds intersection)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        if (right <= x || bottom <= y)
        {
            intersection = default;
            return false;
        }
        intersection = new(x, y, right - x, bottom - y);
        return true;
    }
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

    private SceneTileArea(int width, int height, SceneTile[] ownedTiles)
    {
        Width = width;
        Height = height;
        Tiles = Array.AsReadOnly(ownedTiles);
    }

    public static SceneTileArea Fill(int width, int height, SceneTile tile)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Tile area dimensions must be positive.");
        var tiles = new SceneTile[checked(width * height)];
        Array.Fill(tiles, tile);
        return new SceneTileArea(width, height, tiles);
    }

    /// <summary>Fills an internally owned tile array in a single callback; the temporary span cannot be retained.</summary>
    public static SceneTileArea Create<TState>(int width, int height, TState state, SpanAction<SceneTile, TState> populate)
    {
        ArgumentNullException.ThrowIfNull(populate);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Tile area dimensions must be positive.");
        var tiles = new SceneTile[checked(width * height)];
        populate(tiles, state);
        return new SceneTileArea(width, height, tiles);
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<SceneTile> Tiles { get; }

    public SceneTile this[int x, int y] => Tiles[checked(y * Width + x)];
}

public sealed record SceneObserverScope(bool IsGlobal, IReadOnlySet<PlayerId> Players)
{
    public static SceneObserverScope Global { get; } = new(true, Array.Empty<PlayerId>().ToFrozenSet());
    private static readonly SceneObserverScope[] SingleSlotScopes = Enumerable.Range(0, 256)
        .Select(static slot => new SceneObserverScope(false, new[] { new PlayerId(slot) }.ToFrozenSet())).ToArray();

    public static SceneObserverScope Personal(PlayerId player) => (uint)player.Slot < (uint)SingleSlotScopes.Length
        ? SingleSlotScopes[player.Slot]
        : new(false, new[] { player }.ToFrozenSet());

    public static SceneObserverScope Personal(IEnumerable<PlayerId> players) =>
        players.TryGetNonEnumeratedCount(out int count) && count == 1
            ? Personal(players.First()) : new(false, players.ToFrozenSet());

    /// <summary>When non-null, personal membership is bound to these exact connections.</summary>
    public IReadOnlyDictionary<PlayerId, PlayerSessionId>? Sessions { get; init; }

    public static SceneObserverScope PersonalSessions(IEnumerable<SceneSessionObserver> observers)
    {
        var selected = new Dictionary<PlayerId, PlayerSessionId>();
        foreach (var observer in observers)
        {
            if (selected.TryGetValue(observer.Player, out var previous) && previous != observer.Session)
                throw new ArgumentException("Conflicting sessions for one player slot.", nameof(observers));
            selected[observer.Player] = observer.Session;
        }
        var sessions = selected.ToFrozenDictionary();
        return Personal(sessions.Keys) with { Sessions = sessions };
    }

    public bool Contains(PlayerId playerId) => IsGlobal || Players.Contains(playerId);
}

public readonly record struct SceneSessionObserver(PlayerId Player, PlayerSessionId Session)
{
    public static SceneSessionObserver From(PlayerSnapshot player) => new(player.Id,
        player.SessionId ?? throw new ArgumentException("The player has no active session.", nameof(player)));
    public static SceneSessionObserver From(PlayerSessionOpened opened) => new(opened.PlayerId, opened.SessionId);
}

public sealed record SceneOverlayDefinition(
    OverlayId Id,
    string Name,
    SceneBounds Bounds,
    int Order,
    SceneObserverScope Observers,
    SceneTileArea Tiles,
    bool Enabled = true,
    bool FillOutsideWithAir = false)
{
    // Most overlays are just tiles. Use one nullable reference for optional
    // object metadata rather than adding a field per virtual object kind.
    private VirtualObjects? virtualObjects;
    public IReadOnlyList<SceneVirtualChestDefinition> VirtualChests
    {
        get => virtualObjects?.Chests ?? Array.Empty<SceneVirtualChestDefinition>();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var signs = VirtualSigns;
            var frames = VirtualItemFrames;
            var displays = VirtualDisplays;
            virtualObjects = value.Count == 0 && signs.Count == 0 && frames.Count == 0 && displays.Count == 0 ? null : new(value, signs, frames, displays);
        }
    }
    public IReadOnlyList<SceneVirtualSignDefinition> VirtualSigns
    {
        get => virtualObjects?.Signs ?? Array.Empty<SceneVirtualSignDefinition>();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var chests = VirtualChests;
            var frames = VirtualItemFrames;
            var displays = VirtualDisplays;
            virtualObjects = value.Count == 0 && chests.Count == 0 && frames.Count == 0 && displays.Count == 0 ? null : new(chests, value, frames, displays);
        }
    }
    public IReadOnlyList<SceneVirtualItemFrameDefinition> VirtualItemFrames
    {
        get => virtualObjects?.ItemFrames ?? Array.Empty<SceneVirtualItemFrameDefinition>();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var chests = VirtualChests;
            var signs = VirtualSigns;
            var displays = VirtualDisplays;
            virtualObjects = value.Count == 0 && chests.Count == 0 && signs.Count == 0 && displays.Count == 0 ? null : new(chests, signs, value, displays);
        }
    }
    public IReadOnlyList<SceneVirtualDisplayDefinition> VirtualDisplays
    {
        get => virtualObjects?.Displays ?? Array.Empty<SceneVirtualDisplayDefinition>();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var chests = VirtualChests;
            var signs = VirtualSigns;
            var frames = VirtualItemFrames;
            virtualObjects = value.Count == 0 && chests.Count == 0 && signs.Count == 0 && frames.Count == 0 ? null : new(chests, signs, frames, value);
        }
    }
    private sealed record VirtualObjects(IReadOnlyList<SceneVirtualChestDefinition> Chests,
        IReadOnlyList<SceneVirtualSignDefinition> Signs, IReadOnlyList<SceneVirtualItemFrameDefinition> ItemFrames,
        IReadOnlyList<SceneVirtualDisplayDefinition> Displays);
}

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
    IReadOnlySet<PlayerId> Observers)
{
    /// <summary>Refresh only the native world sections already loaded by each recipient.</summary>
    public bool RefreshLoadedSections { get; init; }
}

public interface ISceneRenderSink
{
    void Invalidate(SceneRenderRequest request);
    /// <summary>True if the renderer accepted responsibility for this update.</summary>
    bool TryInvalidate(SceneRenderRequest request)
    {
        Invalidate(request);
        return true;
    }
}

public interface ISceneLease : IDisposable
{
    SceneId Id { get; }

    OverlayId AddOverlay(SceneOverlayDefinition definition);

    bool UpdateOverlay(OverlayId overlayId, SceneOverlayDefinition definition);

    bool RemoveOverlay(OverlayId overlayId);
    bool BringToFront(OverlayId overlayId) => throw new NotSupportedException("Overlay ordering is unavailable.");
    bool Redraw(OverlayId overlayId) => throw new NotSupportedException("Overlay redraw is unavailable.");
}

public interface ISceneRuntime
{
    bool HasActiveOverlays { get; }
    bool SupportsVirtualChests => false;
    bool SupportsVirtualSigns => false;
    bool SupportsVirtualSignEdits => false;
    bool SupportsVirtualSignUi => false;
    bool SupportsVirtualItemFrames => false;
    bool SupportsVirtualDisplays => false;
    bool HasVirtualItemFrames(WorldId worldId) => false;
    bool HasVirtualDisplays(WorldId worldId) => false;
    IReadOnlyList<SceneVirtualItemFrameSnapshot> SnapshotVisibleVirtualItemFrames(WorldId worldId, SceneSessionObserver observer, SceneBounds region) => [];
    IReadOnlyList<SceneVirtualDisplaySnapshot> SnapshotVisibleVirtualDisplays(WorldId worldId, SceneSessionObserver observer, SceneBounds region) => [];

    bool TryResolveVirtualChest(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualChestSnapshot? chest) { chest = null; return false; }

    bool TryRequestVirtualChestOpen(PlayerSnapshot player, int tileX, int tileY) => false;

    bool TryTakeVirtualChestOpen(SceneId scene, OverlayId overlay, VirtualChestId chest,
        out SceneVirtualChestOpenRequest? request) { request = null; return false; }

    bool TryResolveVirtualSign(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualSignSnapshot? sign) { sign = null; return false; }

    bool TryRequestVirtualSignRead(PlayerSnapshot player, int tileX, int tileY) => false;

    bool TryTakeVirtualSignRead(SceneId scene, OverlayId overlay, VirtualSignId sign,
        out SceneVirtualSignReadRequest? request) { request = null; return false; }
    bool TryRequestVirtualSignEdit(PlayerSnapshot player, int tileX, int tileY, string text) => false;
    bool TryTakeVirtualSignEdit(SceneId scene, OverlayId overlay, VirtualSignId sign,
        out SceneVirtualSignEditRequest? request) { request = null; return false; }
    bool TryApplyVirtualSignEdit(PlayerSnapshot player, SceneVirtualSignEditRequest edit,
        out SceneVirtualSignDefinition? definition) { definition = null; return false; }
    bool TryPresentVirtualSign(PlayerSnapshot player, SceneVirtualSignReadRequest read) => false;
    bool TryRefreshVirtualSign(PlayerSnapshot player, SceneVirtualSignEditRequest edit) => false;
    bool IsVirtualSignIndex(PlayerSnapshot player, short index) => false;
    bool TryRequestPresentedVirtualSignEdit(PlayerSnapshot player, short index, int x, int y, string text) => false;
    void ObserveCanonicalSignRead(PlayerSnapshot player, int x, int y) { }
    void ObserveCanonicalSignPresented(PlayerSnapshot player, short index, int x, int y) { }

    bool HasPersonalAirMask(WorldId worldId, PlayerId observer) =>
        SnapshotScenes().Any(scene => scene.WorldId == worldId && scene.Overlays.Any(overlay =>
            overlay.Enabled && overlay.FillOutsideWithAir && !overlay.Observers.IsGlobal &&
            overlay.Observers.Contains(observer)));

    bool HasOverlayInRegion(WorldId worldId, SceneBounds region, PlayerId? observer = null) =>
        region.Width > 0 && region.Height > 0 && SnapshotScenes().Any(scene => scene.WorldId == worldId && scene.Overlays.Any(overlay =>
            overlay.Enabled && (overlay.Bounds.Intersects(region) || overlay.FillOutsideWithAir) &&
            (observer is null || overlay.Observers.Contains(observer.Value))));

    /// <summary>Enabled overlay rectangles, excluding the implicit personal air background.</summary>
    IEnumerable<SceneOverlayDefinition> EnumerateOverlaysInRegion(WorldId worldId, SceneBounds region) =>
        SnapshotScenes().Where(scene => scene.WorldId == worldId).SelectMany(static scene => scene.Overlays)
            .Where(overlay => region.Width > 0 && region.Height > 0 && overlay.Enabled && overlay.Bounds.Intersects(region));

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
    void SetSessionObservers(IEnumerable<SceneSessionObserver> observers) =>
        throw new NotSupportedException("Session-bound visibility is unavailable.");
    bool AssignPlayerSession(SceneSessionObserver observer) =>
        throw new NotSupportedException("Session-bound visibility is unavailable.");
    bool RemovePlayerSession(SceneSessionObserver observer) =>
        throw new NotSupportedException("Session-bound visibility is unavailable.");
}

public interface ISceneVisibilityRuntime
{
    bool HasAssignments { get; }

    IVisibilityLayerLease CreateLayer(
        WorldId worldId,
        string name,
        string owner,
        IEnumerable<PlayerId> observers);
    IVisibilityLayerLease CreateSessionLayer(WorldId worldId, string name, string owner,
        IEnumerable<SceneSessionObserver> observers) =>
        throw new NotSupportedException("Session-bound visibility is unavailable.");

    bool CanObserve(WorldId worldId, PlayerId observer, SceneEntity entity);

    IReadOnlyList<VisibilityLayerSnapshot> SnapshotLayers();
}
