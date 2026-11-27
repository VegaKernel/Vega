using Vega.Contracts.Scenes;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>Provider-owned logical text with revisioned edits and exact-session read intents.</summary>
public interface IPluginVirtualSign : IDisposable
{
    VirtualSignId Id { get; }
    SceneVirtualSignDefinition Definition { get; }
    bool TrySetText(long expectedRevision, string text);
    bool TryTakeReadRequest(out SceneVirtualSignReadRequest? request);
    bool TryTakeEditRequest(out SceneVirtualSignEditRequest? request) { request = null; return false; }
    /// <summary>Accept an opted-in edit after the plugin authorizes the intent. Revalidates the current observer, visibility, reach and revision.</summary>
    bool TryAcceptEdit(SceneVirtualSignEditRequest request) => false;
    /// <summary>Open the stock sign response for this exact read on the authoritative game thread.</summary>
    bool TryPresentRead(SceneVirtualSignReadRequest request) => false;
    /// <summary>Refresh logical text without opening the UI (e.g. an explicitly rejected edit).</summary>
    bool TryRefreshEdit(SceneVirtualSignEditRequest request) => false;
}

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private bool TryGetEditor(PlayerId id, out PlayerSnapshot? player) => context.Players.TryGet(id, out player);
        private sealed partial class TileProvider
        {
            public IPluginVirtualSign CreateVirtualSign(int localX, int localY, string text) => CreateVirtualSign(localX, localY, text, false);
            public IPluginVirtualSign CreateVirtualSign(int localX, int localY, string text, bool allowEdits) => Read(() =>
            {
                if (!sceneRuntime.SupportsVirtualSigns) throw new NotSupportedException("Virtual signs are unavailable in this host.");
                if (allowEdits && !sceneRuntime.SupportsVirtualSignEdits) throw new NotSupportedException("Virtual sign edits are unavailable in this host.");
                var sign = new SceneVirtualSignDefinition(VirtualSignId.New(), localX, localY, text, 1, allowEdits);
                SetSigns([.. definition.VirtualSigns, sign]);
                return new VirtualSign(this, sign.Id);
            });

            private void SetSigns(SceneVirtualSignDefinition[] signs)
            {
                var next = definition with { VirtualSigns = signs.Length == 0 ? Array.Empty<SceneVirtualSignDefinition>() : Array.AsReadOnly(signs) };
                Require(Scene.UpdateOverlay(Id, next));
                definition = next;
            }

            private SceneVirtualSignDefinition ReadSign(VirtualSignId id) => Read(() =>
                definition.VirtualSigns.FirstOrDefault(sign => sign.Id == id) ??
                    throw new ObjectDisposedException($"Virtual sign {id.Value}"));

            private bool ReplaceSign(VirtualSignId id, long expectedRevision, string text) => Read(() =>
            {
                var current = definition.VirtualSigns.FirstOrDefault(sign => sign.Id == id);
                if (current is null || current.Revision != expectedRevision) return false;
                var next = new SceneVirtualSignDefinition(id, current.LocalX, current.LocalY, text, checked(current.Revision + 1), current.AllowEdits);
                SetSigns(definition.VirtualSigns.Select(sign => sign.Id == id ? next : sign).ToArray());
                return true;
            });

            private void RemoveSign(VirtualSignId id) => Mutate(() =>
            {
                if (definition.VirtualSigns.Any(sign => sign.Id == id))
                    SetSigns(definition.VirtualSigns.Where(sign => sign.Id != id).ToArray());
            });

            private (bool Found, SceneVirtualSignReadRequest? Request) TakeSignRead(VirtualSignId id)
            {
                lock (Owner.sync)
                {
                    AssertAlive();
                    var signs = definition.VirtualSigns;
                    for (int i = 0; i < signs.Count; i++)
                        if (signs[i].Id == id)
                        {
                            bool found = sceneRuntime.TryTakeVirtualSignRead(Scene.Id, Id, id, out var request);
                            return (found, request);
                        }
                    return (false, null);
                }
            }

            private (bool Found, SceneVirtualSignEditRequest? Request) TakeSignEdit(VirtualSignId id)
            {
                lock (Owner.sync)
                {
                    AssertAlive();
                    var signs = definition.VirtualSigns;
                    for (int i = 0; i < signs.Count; i++)
                        if (signs[i].Id == id && signs[i].AllowEdits)
                        {
                            bool found = sceneRuntime.TryTakeVirtualSignEdit(Scene.Id, Id, id, out var request);
                            return (found, request);
                        }
                    return (false, null);
                }
            }

            private bool AcceptSignEdit(VirtualSignId id, SceneVirtualSignEditRequest request) => Read(() =>
            {
                if (request.Sign.SceneId != Scene.Id || request.Sign.OverlayId != Id || request.Sign.Definition?.Id != id ||
                    !Owner.TryGetEditor(request.Observer.Player, out var player) || player is null ||
                    !sceneRuntime.TryApplyVirtualSignEdit(player, request, out var updated)) return false;
                var signs = definition.VirtualSigns.Select(sign => sign.Id == id ? updated! : sign).ToArray();
                definition = definition with { VirtualSigns = Array.AsReadOnly(signs) };
                sceneRuntime.TryRefreshVirtualSign(player, request);
                return true;
            });

            private bool PresentSignRead(VirtualSignId id, SceneVirtualSignReadRequest request) => Read(() =>
                request.Sign.SceneId == Scene.Id && request.Sign.OverlayId == Id && request.Sign.Definition?.Id == id &&
                Owner.TryGetEditor(request.Observer.Player, out var player) && player is not null && sceneRuntime.TryPresentVirtualSign(player, request));

            private bool RefreshSignEdit(VirtualSignId id, SceneVirtualSignEditRequest request) => Read(() =>
                request.Sign.SceneId == Scene.Id && request.Sign.OverlayId == Id && request.Sign.Definition?.Id == id &&
                Owner.TryGetEditor(request.Observer.Player, out var player) && player is not null && sceneRuntime.TryRefreshVirtualSign(player, request));

            private sealed class VirtualSign(TileProvider provider, VirtualSignId id) : IPluginVirtualSign
            {
                private int disposed;
                public VirtualSignId Id => id;
                public SceneVirtualSignDefinition Definition { get { AssertOpen(); return provider.ReadSign(id); } }
                public bool TrySetText(long expectedRevision, string text)
                {
                    AssertOpen();
                    return provider.ReplaceSign(id, expectedRevision, text);
                }
                public bool TryTakeReadRequest(out SceneVirtualSignReadRequest? request)
                {
                    AssertOpen();
                    var result = provider.TakeSignRead(id);
                    request = result.Request;
                    return result.Found;
                }
                public bool TryTakeEditRequest(out SceneVirtualSignEditRequest? request)
                {
                    AssertOpen();
                    var result = provider.TakeSignEdit(id);
                    request = result.Request;
                    return result.Found;
                }
                public bool TryAcceptEdit(SceneVirtualSignEditRequest request)
                {
                    AssertOpen();
                    return provider.AcceptSignEdit(id, request);
                }
                public bool TryPresentRead(SceneVirtualSignReadRequest request)
                {
                    AssertOpen();
                    return provider.PresentSignRead(id, request);
                }
                public bool TryRefreshEdit(SceneVirtualSignEditRequest request)
                {
                    AssertOpen();
                    return provider.RefreshSignEdit(id, request);
                }
                public void Dispose()
                {
                    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                    try { provider.RemoveSign(id); }
                    catch (ObjectDisposedException) { /* The owning provider was already removed. */ }
                }
                private void AssertOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            }
        }
    }
}
