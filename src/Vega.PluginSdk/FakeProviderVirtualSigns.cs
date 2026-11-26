using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

/// <summary>Provider-owned logical text with revisioned edits and exact-session read intents.</summary>
public interface IPluginVirtualSign : IDisposable
{
    VirtualSignId Id { get; }
    SceneVirtualSignDefinition Definition { get; }
    bool TrySetText(long expectedRevision, string text);
    bool TryTakeReadRequest(out SceneVirtualSignReadRequest? request);
}

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private sealed partial class TileProvider
        {
            public IPluginVirtualSign CreateVirtualSign(int localX, int localY, string text) => Read(() =>
            {
                if (!sceneRuntime.SupportsVirtualSigns) throw new NotSupportedException("Virtual signs are unavailable in this host.");
                var sign = new SceneVirtualSignDefinition(VirtualSignId.New(), localX, localY, text, 1);
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
                var next = new SceneVirtualSignDefinition(id, current.LocalX, current.LocalY, text, checked(current.Revision + 1));
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
