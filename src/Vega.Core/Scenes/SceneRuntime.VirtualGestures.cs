using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime : ISceneGestureRuntime
{
    private static readonly TimeSpan MaximumGestureLifetime = TimeSpan.FromSeconds(20);
    private readonly Dictionary<(SceneId Scene, OverlayId Overlay), GestureTargets> gestureTargets = [];
    // Bounded by live exact sessions. A capture keeps begin, latest move and terminal input only.
    private readonly Dictionary<(WorldId World, PlayerId Player), GestureCapture> gestureCaptures = [];
    private int gestureTargetCount;

    public bool HasGestureTargets => Volatile.Read(ref gestureTargetCount) != 0;
    public bool HasPendingGestures { get { lock (sync) return gestureCaptures.Count != 0; } }

    public bool SetGestureTargets(SceneId scene, OverlayId overlay, IReadOnlyList<SceneGestureTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > MaximumVirtualObjectsPerOverlay) throw new ArgumentOutOfRangeException(nameof(targets));
        var selected = targets.ToArray();
        if (selected.Length > MaximumVirtualObjectsPerOverlay) throw new ArgumentOutOfRangeException(nameof(targets));
        lock (sync)
        {
            if (!scenes.TryGetValue(scene, out var owner) || !owner.Overlays.TryGetValue(overlay, out var state)) return false;
            var bounds = state.Definition.Bounds;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < selected.Length; i++)
            {
                var target = selected[i];
                if (target is null || string.IsNullOrWhiteSpace(target.Key) || target.Key.Length > 64 || target.Key.Any(char.IsControl) ||
                    !FitsGestureBounds(target.LocalBounds, bounds) || !keys.Add(target.Key) ||
                    selected.Take(i).Any(other => other.LocalBounds.Intersects(target.LocalBounds)))
                    throw new ArgumentException("Gesture targets must be uniquely keyed, nonoverlapping rectangles within the provider.", nameof(targets));
            }
            RemoveGestures(scene, overlay);
            if (selected.Length != 0) gestureTargets[(scene, overlay)] = new(Guid.NewGuid(), selected);
            Volatile.Write(ref gestureTargetCount, gestureTargets.Count);
            return true;
        }
    }

    private static bool FitsGestureBounds(SceneBounds local, SceneBounds provider) =>
        local.X >= 0 && local.Y >= 0 && local.Width > 0 && local.Height > 0 &&
        (long)local.X + local.Width <= provider.Width && (long)local.Y + local.Height <= provider.Height;

    public bool ObserveGesturePosition(PlayerSnapshot player, int projectileSlot, int projectileKey, WorldTilePosition position, DateTimeOffset now)
    {
        if (projectileSlot < 0) return false;
        if (!TryGetGestureObserver(player, position, out var observer))
        { CancelGesture(player, projectileSlot, projectileKey, now); return false; }
        lock (sync)
        {
            var key = (player.WorldId, player.Id);
            if (gestureCaptures.TryGetValue(key, out var existing) && existing.Observer == observer)
            {
                // A terminal gesture is never resurrected by a late update from the same native projectile.
                if (existing.ProjectileSlot == projectileSlot && existing.ProjectileKey == projectileKey)
                {
                    if (existing.Terminal is not null) return false;
                    if (Expired(existing, now) || !IsVisibleGesture(existing, position)) { FinishGesture(existing, SceneGesturePhase.Cancel, now); return false; }
                    existing.LastSeenAt = now;
                    if (existing.Position != position) { existing.Position = position; existing.MovePending = true; }
                    return true;
                }
                if (existing.Terminal is null) { FinishGesture(existing, SceneGesturePhase.Cancel, now); return false; }
                // Preserve the pending cancellation/end until consumed. A subsequent sample can begin a new capture.
                if (existing.BeginPending || existing.MovePending || existing.TerminalPending) return false;
            }
            if (!TryResolveGestureLocked(player.WorldId, observer, position, out var resolved)) return false;
            gestureCaptures[key] = new(Guid.NewGuid(), player.WorldId, observer, projectileSlot, projectileKey,
                resolved.Scene, resolved.Overlay.Id, resolved.Targets.Generation, resolved.Target, position, now);
            return true;
        }
    }

    public void CancelGesture(PlayerSnapshot player, int projectileSlot, int projectileKey, DateTimeOffset now)
    {
        lock (sync)
            if (gestureCaptures.TryGetValue((player.WorldId, player.Id), out var capture) &&
                capture.Observer.Session == player.SessionId && capture.ProjectileSlot == projectileSlot && capture.ProjectileKey == projectileKey &&
                capture.Terminal is null) FinishGesture(capture, SceneGesturePhase.Cancel, now);
    }

    public bool TryCompleteGesture(PlayerSnapshot player, WorldTilePosition origin, WorldTilePosition position, bool toolHeld, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!gestureCaptures.TryGetValue((player.WorldId, player.Id), out var capture) || capture.Observer.Session != player.SessionId) return false;
            if (capture.Terminal is not null)
            {
                if (capture.ReleaseConsumed || !IsOriginNear(capture, origin)) return false;
                capture.ReleaseConsumed = true;
                return true;
            }
            capture.ReleaseConsumed = true;
            if (toolHeld && !Expired(capture, now) && TryGetGestureObserver(player, origin, out var observer) && observer == capture.Observer &&
                TryGetGestureObserver(player, position, out _) && IsVisibleGesture(capture, origin) && IsVisibleGesture(capture, position) &&
                IsOriginNear(capture, origin))
            {
                capture.Position = position;
                FinishGesture(capture, SceneGesturePhase.End, now);
            }
            else FinishGesture(capture, SceneGesturePhase.Cancel, now);
            return true;
        }
    }

    public void TickGestures(IPlayerDirectory players, ISceneGestureToolState tool, DateTimeOffset now)
    {
        lock (sync)
        {
            foreach (var (key, capture) in gestureCaptures)
            {
                if (!players.TryGet(key.Player, out var player) || player is null || player.WorldId != key.World ||
                    player.SessionId != capture.Observer.Session || !IsVisibleGesture(capture, capture.Origin))
                { gestureCaptures.Remove(key); continue; }
                if (capture.Terminal is not null)
                {
                    if (now - capture.LastSeenAt > VirtualInteractionLifetime) gestureCaptures.Remove(key);
                    continue;
                }
                if (Expired(capture, now) || !TryGetGestureObserver(player, capture.Position, out _) || !tool.IsHoldingGestureTool(player) ||
                    !IsVisibleGesture(capture, capture.Position)) FinishGesture(capture, SceneGesturePhase.Cancel, now);
            }
        }
    }

    public bool TryTakeGesture(SceneId scene, OverlayId overlay, out SceneGestureEvent? input)
    {
        lock (sync)
        {
            foreach (var (key, capture) in gestureCaptures)
            {
                if (capture.Scene != scene || capture.Overlay != overlay) continue;
                if (!IsVisibleGesture(capture, capture.Origin) || DateTimeOffset.UtcNow - capture.LastSeenAt > VirtualInteractionLifetime)
                { gestureCaptures.Remove(key); continue; }
                SceneGesturePhase phase;
                if (capture.BeginPending) { capture.BeginPending = false; phase = SceneGesturePhase.Begin; }
                else if (capture.MovePending) { capture.MovePending = false; phase = SceneGesturePhase.Move; }
                else if (capture.TerminalPending) { capture.TerminalPending = false; phase = capture.Terminal!.Value; }
                else continue;
                input = new(capture.Id, scene, overlay, capture.World, capture.Observer, capture.Target, phase,
                    capture.Origin, phase == SceneGesturePhase.Begin ? capture.Origin : capture.Position,
                    phase == SceneGesturePhase.Begin ? capture.StartedAt : capture.LastSeenAt) { ConfigurationId = capture.Generation };
                return true;
            }
        }
        input = null;
        return false;
    }

    public bool IsCurrentGesture(PlayerSnapshot player, SceneGestureEvent input)
    {
        if (player.WorldId != input.WorldId || player.Id != input.Observer.Player || player.SessionId != input.Observer.Session) return false;
        lock (sync)
            return TryResolveGestureLocked(input.WorldId, input.Observer, input.Origin, out var resolved) &&
                resolved.Scene == input.SceneId && resolved.Overlay.Id == input.OverlayId && resolved.Targets.Generation == input.ConfigurationId &&
                resolved.Target == input.Target && (input.Phase == SceneGesturePhase.Cancel ||
                    (TryGetGestureObserver(player, input.Position, out _) && TryResolveGestureLocked(input.WorldId, input.Observer, input.Position, out var position) &&
                     position.Scene == input.SceneId && position.Overlay.Id == input.OverlayId && position.Targets.Generation == input.ConfigurationId && position.Target == input.Target));
    }

    private static bool TryGetGestureObserver(PlayerSnapshot player, WorldTilePosition position, out SceneSessionObserver observer) =>
        TryGetInteractionObserver(player, position.X, position.Y, out observer) &&
        (player.Connection?.LoginPhase is not { } phase || phase == ConnectionLoginPhase.FullyConnected) && WithinObjectReach(player, position.X, position.Y);

    private bool IsVisibleGesture(GestureCapture capture, WorldTilePosition position) =>
        TryResolveGestureLocked(capture.World, capture.Observer, position, out var current) && current.Scene == capture.Scene &&
        current.Overlay.Id == capture.Overlay && current.Targets.Generation == capture.Generation && current.Target == capture.Target;

    private bool TryResolveGestureLocked(WorldId world, SceneSessionObserver observer, WorldTilePosition position, out ResolvedGesture result)
    {
        result = default;
        if (gestureTargets.Count == 0 || !sessions.TryGetValue((world, observer.Player), out var session) || session != observer.Session ||
            !ReadLookup().TryGetValue(world, out var lookup) || VisibleProvider(lookup, observer.Player, position.X, position.Y) is not { } definition ||
            !TryFindOwningScene(world, definition, out var scene) || !gestureTargets.TryGetValue((scene, definition.Id), out var targets)) return false;
        foreach (var target in targets.Targets)
        {
            if (!FitsGestureBounds(target.LocalBounds, definition.Bounds) || !target.LocalBounds.Contains(position.X - definition.Bounds.X, position.Y - definition.Bounds.Y)) continue;
            result = new(scene, definition, targets, target);
            return true;
        }
        return false;
    }

    private static bool Expired(GestureCapture capture, DateTimeOffset now) => now - capture.LastSeenAt > VirtualInteractionLifetime || now - capture.StartedAt > MaximumGestureLifetime;
    private static bool IsOriginNear(GestureCapture capture, WorldTilePosition origin) =>
        Math.Abs((long)capture.Origin.X - origin.X) <= 1 && Math.Abs((long)capture.Origin.Y - origin.Y) <= 1;

    private static void FinishGesture(GestureCapture capture, SceneGesturePhase phase, DateTimeOffset now)
    {
        capture.Terminal = phase;
        capture.TerminalPending = true;
        capture.LastSeenAt = now;
        if (phase == SceneGesturePhase.Cancel) capture.MovePending = false;
    }

    private void RemoveGestures(SceneId scene, OverlayId? overlay = null)
    {
        foreach (var (key, capture) in gestureCaptures)
            if (capture.Scene == scene && (overlay is null || capture.Overlay == overlay)) gestureCaptures.Remove(key);
        foreach (var key in gestureTargets.Keys)
            if (key.Scene == scene && (overlay is null || key.Overlay == overlay)) gestureTargets.Remove(key);
        Volatile.Write(ref gestureTargetCount, gestureTargets.Count);
    }

    private void InvalidateGestureCaptures(SceneId scene, OverlayId overlay)
    {
        foreach (var (key, capture) in gestureCaptures)
            if (capture.Scene == scene && capture.Overlay == overlay) gestureCaptures.Remove(key);
        if (gestureTargets.TryGetValue((scene, overlay), out var targets))
            gestureTargets[(scene, overlay)] = targets with { Generation = Guid.NewGuid() };
    }

    private sealed record GestureTargets(Guid Generation, SceneGestureTarget[] Targets);
    private readonly record struct ResolvedGesture(SceneId Scene, SceneOverlayDefinition Overlay, GestureTargets Targets, SceneGestureTarget Target);
    private sealed class GestureCapture(Guid id, WorldId world, SceneSessionObserver observer, int projectileSlot, int projectileKey,
        SceneId scene, OverlayId overlay, Guid generation, SceneGestureTarget target, WorldTilePosition origin, DateTimeOffset startedAt)
    {
        public Guid Id { get; } = id;
        public WorldId World { get; } = world;
        public SceneSessionObserver Observer { get; } = observer;
        public int ProjectileSlot { get; } = projectileSlot;
        public int ProjectileKey { get; } = projectileKey;
        public SceneId Scene { get; } = scene;
        public OverlayId Overlay { get; } = overlay;
        public Guid Generation { get; } = generation;
        public SceneGestureTarget Target { get; } = target;
        public WorldTilePosition Origin { get; } = origin;
        public WorldTilePosition Position { get; set; } = origin;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastSeenAt { get; set; } = startedAt;
        public bool BeginPending { get; set; } = true;
        public bool MovePending { get; set; }
        public SceneGesturePhase? Terminal { get; set; }
        public bool TerminalPending { get; set; }
        public bool ReleaseConsumed { get; set; }
    }
}
