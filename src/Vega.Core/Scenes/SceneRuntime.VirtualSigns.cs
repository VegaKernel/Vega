using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualSignReadRequest> pendingVirtualSignReads = [];
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualSignReadRequest> virtualSignEditors = [];
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualSignEditRequest> pendingVirtualSignEdits = [];
    private static readonly TimeSpan VirtualSignEditorLifetime = TimeSpan.FromMinutes(2);
    public bool SupportsVirtualSigns => true;
    public bool SupportsVirtualSignEdits => true;

    public bool TryResolveVirtualSign(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualSignSnapshot? sign)
    {
        lock (sync) return TryResolveVirtualSignLocked(worldId, observer, tileX, tileY, out sign);
    }

    private bool TryResolveVirtualSignLocked(WorldId worldId, SceneSessionObserver observer, int tileX, int tileY,
        out SceneVirtualSignSnapshot? sign)
    {
        sign = null;
        if (!sessions.TryGetValue((worldId, observer.Player), out var current) || current != observer.Session ||
            !ReadLookup().TryGetValue(worldId, out var world)) return false;
        var definition = VisibleProvider(world, observer.Player, tileX, tileY);
        if (definition is null || definition.VirtualSigns.Count == 0) return false;
        int localX = tileX - definition.Bounds.X, localY = tileY - definition.Bounds.Y;
        for (int i = 0; i < definition.VirtualSigns.Count; i++)
        {
            var candidate = definition.VirtualSigns[i];
            if (localX < candidate.LocalX || localX >= candidate.LocalX + 2 || localY < candidate.LocalY || localY >= candidate.LocalY + 2) continue;
            if (!FootprintVisible(world, observer.Player, definition, candidate.LocalX, candidate.LocalY) ||
                !TryFindOwningScene(worldId, definition, out SceneId scene)) return false;
            sign = new(scene, definition.Id, worldId, new(definition.Bounds.X + candidate.LocalX, definition.Bounds.Y + candidate.LocalY), candidate);
            return true;
        }
        return false;
    }

    public bool TryRequestVirtualSignRead(PlayerSnapshot player, int tileX, int tileY)
    {
        if (!TryGetInteractionObserver(player, tileX, tileY, out var observer)) return false;
        lock (sync)
        {
            if (!TryResolveVirtualSignLocked(player.WorldId, observer, tileX, tileY, out var resolved) || resolved is not { } sign ||
                !WithinObjectReach(player, sign.Position.X, sign.Position.Y)) return false;
            var key = (player.WorldId, player.Id);
            var read = new SceneVirtualSignReadRequest(sign, observer, DateTimeOffset.UtcNow);
            pendingVirtualSignReads[key] = read;
            pendingVirtualSignEdits.Remove(key);
            if (sign.Definition.AllowEdits) virtualSignEditors[key] = read;
            else virtualSignEditors.Remove(key);
            return true;
        }
    }

    public bool TryTakeVirtualSignRead(SceneId scene, OverlayId overlay, VirtualSignId sign,
        out SceneVirtualSignReadRequest? request)
    {
        lock (sync)
        {
            while (true)
            {
                SceneVirtualSignReadRequest? pending = null;
                foreach (var value in pendingVirtualSignReads.Values)
                    if (value.Sign.SceneId == scene && value.Sign.OverlayId == overlay && value.Sign.Definition.Id == sign)
                    { pending = value; break; }
                if (pending is not { } intent) break;
                pendingVirtualSignReads.Remove((intent.Sign.WorldId, intent.Observer.Player));
                var position = intent.Sign.Position;
                if (DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime ||
                    !TryResolveVirtualSignLocked(intent.Sign.WorldId, intent.Observer, position.X, position.Y, out var resolved) ||
                    resolved is not { } current || current.SceneId != scene || current.OverlayId != overlay ||
                    current.Definition.Id != sign || current.Position != position) continue;
                request = intent with { Sign = current };
                return true;
            }
            request = null;
            return false;
        }
    }

    private void RemoveVirtualSignReads(SceneId scene, OverlayId? overlay = null)
    {
        List<(WorldId World, PlayerId Player)>? removed = null;
        foreach (var pair in pendingVirtualSignReads)
            if (pair.Value.Sign.SceneId == scene && (overlay is null || pair.Value.Sign.OverlayId == overlay.Value))
                (removed ??= []).Add(pair.Key);
        if (removed is not null)
            foreach (var key in removed) pendingVirtualSignReads.Remove(key);
        removed = null;
        foreach (var pair in virtualSignEditors)
            if (pair.Value.Sign.SceneId == scene && (overlay is null || pair.Value.Sign.OverlayId == overlay.Value))
                (removed ??= []).Add(pair.Key);
        if (removed is not null)
            foreach (var key in removed) virtualSignEditors.Remove(key);
        removed = null;
        foreach (var pair in pendingVirtualSignEdits)
            if (pair.Value.Sign.SceneId == scene && (overlay is null || pair.Value.Sign.OverlayId == overlay.Value))
                (removed ??= []).Add(pair.Key);
        if (removed is not null)
            foreach (var key in removed) pendingVirtualSignEdits.Remove(key);
    }

    public bool TryRequestVirtualSignEdit(PlayerSnapshot player, int tileX, int tileY, string text)
    {
        if (!SceneVirtualSignDefinition.IsValidText(text) || !TryGetInteractionObserver(player, tileX, tileY, out var observer)) return false;
        lock (sync)
        {
            var key = (player.WorldId, player.Id);
            if (!virtualSignEditors.TryGetValue(key, out var read) || read.Observer != observer ||
                DateTimeOffset.UtcNow - read.RequestedAt > VirtualSignEditorLifetime ||
                !TryResolveVirtualSignLocked(player.WorldId, observer, tileX, tileY, out var resolved) ||
                resolved is not { } current || !SameEditableSign(read.Sign, current) ||
                !WithinObjectReach(player, current.Position.X, current.Position.Y)) return false;
            virtualSignEditors.Remove(key); // one edit intent per read
            pendingVirtualSignEdits[key] = new(current, observer, text, DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static bool SameEditableSign(SceneVirtualSignSnapshot requested, SceneVirtualSignSnapshot current) =>
        current.Definition.AllowEdits && requested.SceneId == current.SceneId && requested.OverlayId == current.OverlayId &&
        requested.WorldId == current.WorldId && requested.Position == current.Position && ReferenceEquals(requested.Definition, current.Definition);

    public bool TryTakeVirtualSignEdit(SceneId scene, OverlayId overlay, VirtualSignId sign, out SceneVirtualSignEditRequest? request)
    {
        lock (sync)
        {
            while (true)
            {
                SceneVirtualSignEditRequest? pending = null;
                foreach (var candidate in pendingVirtualSignEdits.Values)
                    if (candidate.Sign.SceneId == scene && candidate.Sign.OverlayId == overlay && candidate.Sign.Definition.Id == sign)
                    { pending = candidate; break; }
                if (pending is not { } intent) break;
                pendingVirtualSignEdits.Remove((intent.Sign.WorldId, intent.Observer.Player));
                if (DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime ||
                    !TryResolveVirtualSignLocked(intent.Sign.WorldId, intent.Observer, intent.Sign.Position.X, intent.Sign.Position.Y, out var resolved) ||
                    resolved is not { } current || !SameEditableSign(intent.Sign, current)) continue;
                request = intent;
                return true;
            }
            request = null;
            return false;
        }
    }

    public bool TryApplyVirtualSignEdit(PlayerSnapshot player, SceneVirtualSignEditRequest edit,
        out SceneVirtualSignDefinition? definition)
    {
        definition = null;
        var sign = edit.Sign;
        if (player.WorldId != sign.WorldId || !TryGetInteractionObserver(player, sign.Position.X, sign.Position.Y, out var observer) ||
            observer != edit.Observer || !WithinObjectReach(player, sign.Position.X, sign.Position.Y) ||
            !SceneVirtualSignDefinition.IsValidText(edit.Text) || DateTimeOffset.UtcNow - edit.RequestedAt > VirtualInteractionLifetime) return false;
        lock (sync)
        {
            if (!TryResolveVirtualSignLocked(sign.WorldId, edit.Observer, sign.Position.X, sign.Position.Y, out var resolved) ||
                resolved is not { } current || !SameEditableSign(sign, current)) return false;
            var replacement = new SceneVirtualSignDefinition(current.Definition.Id, current.Definition.LocalX,
                current.Definition.LocalY, edit.Text, checked(current.Definition.Revision + 1), true);
            var scene = scenes[current.SceneId];
            var previous = scene.Overlays[current.OverlayId];
            var signs = previous.Definition.VirtualSigns.Select(candidate => candidate.Id == replacement.Id ? replacement : candidate).ToArray();
            scene.Overlays[current.OverlayId] = previous with { Definition = previous.Definition with { VirtualSigns = Array.AsReadOnly(signs) } };
            Volatile.Write(ref lookup, null);
            definition = replacement;
            return true;
        }
    }
}
