using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    private readonly Dictionary<(WorldId World, PlayerId Player), SceneVirtualSignReadRequest> pendingVirtualSignReads = [];
    public bool SupportsVirtualSigns => true;

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
            pendingVirtualSignReads[(player.WorldId, player.Id)] = new(sign, observer, DateTimeOffset.UtcNow);
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
        if (pendingVirtualSignReads.Count == 0) return;
        List<(WorldId World, PlayerId Player)>? removed = null;
        foreach (var pair in pendingVirtualSignReads)
            if (pair.Value.Sign.SceneId == scene && (overlay is null || pair.Value.Sign.OverlayId == overlay.Value))
                (removed ??= []).Add(pair.Key);
        if (removed is not null)
            foreach (var key in removed) pendingVirtualSignReads.Remove(key);
    }
}
