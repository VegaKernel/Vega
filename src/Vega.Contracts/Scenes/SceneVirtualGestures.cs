using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

/// <summary>Provider-local rectangle accepting a stock WireKite gesture. Rectangles must not overlap.</summary>
public sealed record SceneGestureTarget(string Key, SceneBounds LocalBounds);

public enum SceneGesturePhase { Begin, Move, End, Cancel }

/// <summary>Detached UI input. Begin/Move/End are observations, not authorization for a world mutation.</summary>
public sealed record SceneGestureEvent(Guid GestureId, SceneId SceneId, OverlayId OverlayId, WorldId WorldId,
    SceneSessionObserver Observer, SceneGestureTarget Target, SceneGesturePhase Phase,
    WorldTilePosition Origin, WorldTilePosition Position, DateTimeOffset ObservedAt)
{
    public Guid ConfigurationId { get; init; }
}

/// <summary>Host-only exact-session check of the currently held native input tool.</summary>
public interface ISceneGestureToolState
{
    bool IsHoldingGestureTool(PlayerSnapshot player);
}

/// <summary>Host-only routing from accepted native input; plugins only receive lease-scoped events.</summary>
public interface ISceneGestureRuntime
{
    bool HasGestureTargets { get; }
    bool HasPendingGestures { get; }
    bool SetGestureTargets(SceneId scene, OverlayId overlay, IReadOnlyList<SceneGestureTarget> targets);
    bool ObserveGesturePosition(PlayerSnapshot player, int projectileSlot, int projectileKey, WorldTilePosition position, DateTimeOffset now);
    void CancelGesture(PlayerSnapshot player, int projectileSlot, int projectileKey, DateTimeOffset now);
    /// <summary>Returns true if an active UI capture consumed the gesture release, even if it was cancelled.</summary>
    bool TryCompleteGesture(PlayerSnapshot player, WorldTilePosition origin, WorldTilePosition position, bool toolHeld, DateTimeOffset now);
    void TickGestures(IPlayerDirectory players, ISceneGestureToolState tool, DateTimeOffset now);
    bool TryTakeGesture(SceneId scene, OverlayId overlay, out SceneGestureEvent? input);
    bool IsCurrentGesture(PlayerSnapshot player, SceneGestureEvent input);
}
