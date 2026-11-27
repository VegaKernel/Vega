using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Scenes;

/// <summary>Opt-in virtual tile input target. Render a stock switch at this local coordinate.</summary>
public sealed record SceneButtonTarget(string Key, int LocalX, int LocalY);

/// <summary>Detached input intent from a current observer of a virtual tile. It never executes native wiring.</summary>
public sealed record SceneButtonPress(SceneId SceneId, OverlayId OverlayId, WorldId WorldId, SceneSessionObserver Observer,
    SceneButtonTarget Target, WorldTilePosition Position, DateTimeOffset RequestedAt)
{
    /// <summary>Opaque target registration/overlay revision, not a transferable authorization token.</summary>
    public Guid ConfigurationId { get; init; }
}

/// <summary>Host-only virtual input authority; plugins get the lease-scoped facade, not this routing API.</summary>
public interface ISceneButtonRuntime
{
    bool SetButtonTargets(SceneId scene, OverlayId overlay, IReadOnlyList<SceneButtonTarget> targets);
    bool TryRequestButtonPress(PlayerSnapshot player, int tileX, int tileY);
    bool TryTakeButtonPress(SceneId scene, OverlayId overlay, out SceneButtonPress? press);
    bool IsCurrentButtonPress(PlayerSnapshot player, SceneButtonPress press);
}
