using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

public interface IPluginVirtualDisplay : IDisposable
{
    VirtualDisplayId Id { get; }
    SceneVirtualDisplayDefinition Definition { get; }
    bool TryReplace(long expectedRevision, IReadOnlyList<SceneDisplayItem> items, IReadOnlyList<SceneDisplayItem> dyes);
}

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private sealed partial class TileProvider
        {
            public IPluginVirtualDisplay CreateVirtualDisplay(SceneVirtualDisplayKind kind, int localX, int localY,
                IReadOnlyList<SceneDisplayItem> items, IReadOnlyList<SceneDisplayItem> dyes) => Read(() =>
            {
                if (!sceneRuntime.SupportsVirtualDisplays) throw new NotSupportedException("Virtual displays are unavailable in this host.");
                var display = new SceneVirtualDisplayDefinition(VirtualDisplayId.New(), kind, localX, localY, items, dyes, 1);
                SetDisplays([.. definition.VirtualDisplays, display]);
                return new VirtualDisplay(this, display.Id);
            });

            private void SetDisplays(SceneVirtualDisplayDefinition[] displays)
            {
                var next = definition with { VirtualDisplays = displays.Length == 0 ? Array.Empty<SceneVirtualDisplayDefinition>() : Array.AsReadOnly(displays) };
                Require(Scene.UpdateOverlay(Id, next));
                definition = next;
            }

            private SceneVirtualDisplayDefinition ReadDisplay(VirtualDisplayId id) => Read(() =>
                definition.VirtualDisplays.FirstOrDefault(display => display.Id == id) ?? throw new ObjectDisposedException($"Virtual display {id.Value}"));

            private bool ReplaceDisplay(VirtualDisplayId id, long expectedRevision, IReadOnlyList<SceneDisplayItem> items, IReadOnlyList<SceneDisplayItem> dyes) => Read(() =>
            {
                var current = definition.VirtualDisplays.FirstOrDefault(display => display.Id == id);
                if (current is null || current.Revision != expectedRevision) return false;
                var next = new SceneVirtualDisplayDefinition(id, current.Kind, current.LocalX, current.LocalY, items, dyes, checked(current.Revision + 1));
                SetDisplays(definition.VirtualDisplays.Select(display => display.Id == id ? next : display).ToArray());
                return true;
            });

            private void RemoveDisplay(VirtualDisplayId id) => Mutate(() =>
            {
                if (definition.VirtualDisplays.Any(display => display.Id == id)) SetDisplays(definition.VirtualDisplays.Where(display => display.Id != id).ToArray());
            });

            private sealed class VirtualDisplay(TileProvider provider, VirtualDisplayId id) : IPluginVirtualDisplay
            {
                private int disposed;
                public VirtualDisplayId Id => id;
                public SceneVirtualDisplayDefinition Definition { get { AssertOpen(); return provider.ReadDisplay(id); } }
                public bool TryReplace(long expectedRevision, IReadOnlyList<SceneDisplayItem> items, IReadOnlyList<SceneDisplayItem> dyes)
                {
                    AssertOpen();
                    return provider.ReplaceDisplay(id, expectedRevision, items, dyes);
                }
                public void Dispose()
                {
                    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                    try { provider.RemoveDisplay(id); }
                    catch (ObjectDisposedException) { /* The owning provider was already removed. */ }
                }
                private void AssertOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            }
        }
    }
}
