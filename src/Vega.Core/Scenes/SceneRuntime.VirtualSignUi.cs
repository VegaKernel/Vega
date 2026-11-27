using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime
{
    private const int MaximumVirtualSignIndicesPerSession = 32;
    private readonly Dictionary<(WorldId World, PlayerId Player), VirtualSignPresentation> virtualSignPresentations = [];

    private sealed class VirtualSignPresentation
    {
        // Keep assigned client indices reserved for interception after a provider moves or
        // unloads. An actual canonical read/response can reclaim one, or session closure clears them.
        public HashSet<short> Indices { get; } = [];
        public short ActiveIndex = -1;
        public SceneVirtualSignReadRequest? Active;
        public (int X, int Y, DateTimeOffset Time)? CanonicalRead;
    }

    public bool SupportsVirtualSignUi => sink is ISceneVirtualSignUiSink;

    public bool TryPresentVirtualSign(PlayerSnapshot player, SceneVirtualSignReadRequest read)
    {
        if (sink is not ISceneVirtualSignUiSink presenter || !presenter.TryGetVirtualSignIndex(player, read, out short index) || index < 0)
            return false;
        var position = read.Sign.Position;
        lock (sync)
        {
            if (!TryGetInteractionObserver(player, position.X, position.Y, out var observer) || observer != read.Observer ||
                player.WorldId != read.Sign.WorldId || !WithinObjectReach(player, position.X, position.Y) ||
                DateTimeOffset.UtcNow - read.RequestedAt > VirtualInteractionLifetime ||
                !TryResolveVirtualSignLocked(player.WorldId, observer, position.X, position.Y, out var resolved) ||
                resolved is not { } current || !SameSign(read.Sign, current)) return false;
            var key = (player.WorldId, player.Id);
            if (!virtualSignPresentations.TryGetValue(key, out var presentation))
                virtualSignPresentations[key] = presentation = new VirtualSignPresentation();
            if (presentation.Indices.Count >= MaximumVirtualSignIndicesPerSession && !presentation.Indices.Contains(index)) return false;
            presentation.Indices.Add(index); // Reserve before handing bytes to the native socket.
            presentation.ActiveIndex = index;
            presentation.Active = read;
            presentation.CanonicalRead = null;
            if (current.Definition.AllowEdits) virtualSignEditors[key] = read;
            else virtualSignEditors.Remove(key);
        }
        return presenter.TrySendVirtualSign(player, read.Sign, index, open: true);
    }

    public bool IsVirtualSignIndex(PlayerSnapshot player, short index)
    {
        lock (sync) return IsCurrentObserver(player) && virtualSignPresentations.TryGetValue((player.WorldId, player.Id), out var state) && state.Indices.Contains(index);
    }

    public bool TryRequestPresentedVirtualSignEdit(PlayerSnapshot player, short index, int x, int y, string text)
    {
        lock (sync)
        {
            var key = (player.WorldId, player.Id);
            if (!IsCurrentObserver(player) || !virtualSignPresentations.TryGetValue(key, out var state) ||
                state.ActiveIndex != index || state.Active is not { } active || active.Observer.Session != player.SessionId ||
                active.Sign.Position.X != x || active.Sign.Position.Y != y || !virtualSignEditors.TryGetValue(key, out var editor) ||
                !SameSign(active.Sign, editor.Sign)) return false;
            return TryRequestVirtualSignEditCore(player, x, y, text, presented: true);
        }
    }

    public bool TryRefreshVirtualSign(PlayerSnapshot player, SceneVirtualSignEditRequest edit)
    {
        if (sink is not ISceneVirtualSignUiSink presenter) return false;
        short index;
        SceneVirtualSignSnapshot current;
        lock (sync)
        {
            var sign = edit.Sign;
            if (!TryGetInteractionObserver(player, sign.Position.X, sign.Position.Y, out var observer) || observer != edit.Observer ||
                player.WorldId != sign.WorldId || !WithinObjectReach(player, sign.Position.X, sign.Position.Y) ||
                !virtualSignPresentations.TryGetValue((player.WorldId, player.Id), out var state) || state.Active is not { } active ||
                !SameSign(active.Sign, sign) || !TryResolveVirtualSignLocked(player.WorldId, observer, sign.Position.X, sign.Position.Y, out var resolved) ||
                resolved is not { } found || found.SceneId != sign.SceneId || found.OverlayId != sign.OverlayId || found.Definition.Id != sign.Definition.Id)
                return false;
            index = state.ActiveIndex;
            current = found;
        }
        return presenter.TrySendVirtualSign(player, current, index, open: false);
    }

    public void ObserveCanonicalSignRead(PlayerSnapshot player, int x, int y)
    {
        lock (sync)
            if (IsCurrentObserver(player) && virtualSignPresentations.TryGetValue((player.WorldId, player.Id), out var state))
                state.CanonicalRead = (x, y, DateTimeOffset.UtcNow);
    }

    public void ObserveCanonicalSignPresented(PlayerSnapshot player, short index, int x, int y)
    {
        lock (sync)
        {
            var key = (player.WorldId, player.Id);
            if (!IsCurrentObserver(player) || !virtualSignPresentations.TryGetValue(key, out var state) || state.CanonicalRead is not { } read ||
                DateTimeOffset.UtcNow - read.Time > VirtualInteractionLifetime || read.X < x || read.X >= x + 2 || read.Y < y || read.Y >= y + 2) return;
            state.CanonicalRead = null;
            if (!state.Indices.Remove(index)) return;
            if (state.ActiveIndex == index)
            {
                state.ActiveIndex = -1;
                state.Active = null;
                virtualSignEditors.Remove(key);
            }
            if (state.Indices.Count == 0) virtualSignPresentations.Remove(key);
        }
    }

    private bool IsCurrentObserver(PlayerSnapshot player) => player.SessionId is { } session &&
        sessions.TryGetValue((player.WorldId, player.Id), out var current) && current == session;

    private static bool SameSign(SceneVirtualSignSnapshot requested, SceneVirtualSignSnapshot current) =>
        requested.SceneId == current.SceneId && requested.OverlayId == current.OverlayId && requested.WorldId == current.WorldId &&
        requested.Position == current.Position && ReferenceEquals(requested.Definition, current.Definition);
}
