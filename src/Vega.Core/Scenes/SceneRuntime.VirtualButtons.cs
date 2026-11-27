using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime : ISceneButtonRuntime
{
    private readonly Dictionary<(SceneId Scene, OverlayId Overlay), ButtonTargets> buttonTargets = [];
    // One outstanding input per exact session, bounded by the live player slots. A newer input replaces it.
    private readonly Dictionary<(WorldId World, PlayerId Player), PendingButton> pendingButtonPresses = [];

    public bool SetButtonTargets(SceneId scene, OverlayId overlay, IReadOnlyList<SceneButtonTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > MaximumVirtualObjectsPerOverlay) throw new ArgumentOutOfRangeException(nameof(targets));
        // Materialize caller-owned collections before entering the world mutation lock.
        var selected = targets.ToArray();
        if (selected.Length > MaximumVirtualObjectsPerOverlay) throw new ArgumentOutOfRangeException(nameof(targets));
        lock (sync)
        {
            if (!scenes.TryGetValue(scene, out var owner) || !owner.Overlays.TryGetValue(overlay, out var state)) return false;
            var definition = state.Definition;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var positions = new Dictionary<(int X, int Y), SceneButtonTarget>();
            foreach (var target in selected)
            {
                if (target is null || string.IsNullOrWhiteSpace(target.Key) || target.Key.Length > 64 || target.Key.Any(char.IsControl) ||
                    target.LocalX < 0 || target.LocalY < 0 || target.LocalX >= definition.Bounds.Width || target.LocalY >= definition.Bounds.Height ||
                    (definition.Tiles[target.LocalX, target.LocalY].Flags & SceneTileFlags.Active) == 0 ||
                    !keys.Add(target.Key) || !positions.TryAdd((target.LocalX, target.LocalY), target))
                    throw new ArgumentException("Button targets must have unique keys and distinct active tiles within the provider.", nameof(targets));
            }
            RemoveButtonPresses(scene, overlay);
            buttonTargets.Remove((scene, overlay));
            if (positions.Count != 0) buttonTargets[(scene, overlay)] = new(positions, Guid.NewGuid());
            return true;
        }
    }

    public bool TryRequestButtonPress(PlayerSnapshot player, int tileX, int tileY)
    {
        if (!TryGetButtonObserver(player, tileX, tileY, out var observer)) return false;
        lock (sync)
        {
            if (!TryResolveButtonLocked(player.WorldId, observer, tileX, tileY, out var target)) return false;
            var press = new SceneButtonPress(target.Scene, target.Definition.Id, player.WorldId, observer, target.Target,
                new(tileX, tileY), DateTimeOffset.UtcNow) { ConfigurationId = target.Targets.Generation };
            pendingButtonPresses[(player.WorldId, player.Id)] = new(press, target.Definition, target.Targets);
            return true;
        }
    }

    public bool TryTakeButtonPress(SceneId scene, OverlayId overlay, out SceneButtonPress? press)
    {
        lock (sync)
        {
            while (true)
            {
                PendingButton? found = null;
                foreach (var pending in pendingButtonPresses.Values)
                    if (pending.Press.SceneId == scene && pending.Press.OverlayId == overlay) { found = pending; break; }
                if (found is null) break;
                var intent = found.Press;
                pendingButtonPresses.Remove((intent.WorldId, intent.Observer.Player));
                if (DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime ||
                    !TryResolveButtonLocked(intent.WorldId, intent.Observer, intent.Position.X, intent.Position.Y, out var current) ||
                    current.Scene != scene || !ReferenceEquals(current.Definition, found.Definition) ||
                    !ReferenceEquals(current.Targets, found.Targets) || current.Target != intent.Target) continue;
                press = intent;
                return true;
            }
            press = null;
            return false;
        }
    }

    public bool IsCurrentButtonPress(PlayerSnapshot player, SceneButtonPress press)
    {
        if (player.WorldId != press.WorldId || !TryGetButtonObserver(player, press.Position.X, press.Position.Y, out var observer) ||
            observer != press.Observer || DateTimeOffset.UtcNow - press.RequestedAt > VirtualInteractionLifetime) return false;
        lock (sync)
            return TryResolveButtonLocked(press.WorldId, observer, press.Position.X, press.Position.Y, out var target) &&
                target.Scene == press.SceneId && target.Definition.Id == press.OverlayId && target.Target == press.Target &&
                target.Targets.Generation == press.ConfigurationId;
    }

    private static bool TryGetButtonObserver(PlayerSnapshot player, int x, int y, out SceneSessionObserver observer)
    {
        return TryGetInteractionObserver(player, x, y, out observer) &&
            (player.Connection?.LoginPhase is not { } phase || phase == ConnectionLoginPhase.FullyConnected) && WithinObjectReach(player, x, y);
    }

    private bool TryResolveButtonLocked(WorldId worldId, SceneSessionObserver observer, int x, int y, out ResolvedButton result)
    {
        result = default;
        if (buttonTargets.Count == 0 || !sessions.TryGetValue((worldId, observer.Player), out var session) || session != observer.Session ||
            !ReadLookup().TryGetValue(worldId, out var world) || VisibleProvider(world, observer.Player, x, y) is not { } definition ||
            !TryFindOwningScene(worldId, definition, out var scene) || !buttonTargets.TryGetValue((scene, definition.Id), out var targets)) return false;
        int localX = x - definition.Bounds.X, localY = y - definition.Bounds.Y;
        if (!targets.Positions.TryGetValue((localX, localY), out var target) ||
            (definition.Tiles[localX, localY].Flags & SceneTileFlags.Active) == 0) return false;
        result = new(scene, definition, targets, target);
        return true;
    }

    private void RemoveButtonPresses(SceneId scene, OverlayId? overlay = null)
    {
        if (pendingButtonPresses.Count == 0) return;
        List<(WorldId World, PlayerId Player)>? removed = null;
        foreach (var (key, pending) in pendingButtonPresses)
            if (pending.Press.SceneId == scene && (overlay is null || pending.Press.OverlayId == overlay.Value)) (removed ??= []).Add(key);
        if (removed is not null) foreach (var key in removed) pendingButtonPresses.Remove(key);
    }

    private void RemoveButtons(SceneId scene, OverlayId? overlay = null)
    {
        RemoveButtonPresses(scene, overlay);
        if (overlay is { } id) buttonTargets.Remove((scene, id));
        else
            foreach (var key in buttonTargets.Keys.Where(key => key.Scene == scene).ToArray()) buttonTargets.Remove(key);
    }

    private void InvalidateButtons(SceneId scene, OverlayId overlay)
    {
        RemoveButtonPresses(scene, overlay);
        if (buttonTargets.TryGetValue((scene, overlay), out var targets))
            buttonTargets[(scene, overlay)] = targets with { Generation = Guid.NewGuid() };
    }

    private sealed record ButtonTargets(Dictionary<(int X, int Y), SceneButtonTarget> Positions, Guid Generation);
    private readonly record struct ResolvedButton(SceneId Scene, SceneOverlayDefinition Definition, ButtonTargets Targets, SceneButtonTarget Target);
    private sealed record PendingButton(SceneButtonPress Press, SceneOverlayDefinition Definition, ButtonTargets Targets);
}
