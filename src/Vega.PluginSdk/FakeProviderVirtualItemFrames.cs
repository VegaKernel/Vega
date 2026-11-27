using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

/// <summary>Read-only native item presentation attached to a provider's complete item-frame footprint.</summary>
public interface IPluginVirtualItemFrame : IDisposable
{
    VirtualItemFrameId Id { get; }
    SceneVirtualItemFrameDefinition Definition { get; }
    bool TryReplace(long expectedRevision, int itemType, int stack, byte prefix = 0);
}

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private sealed partial class TileProvider
        {
            public IPluginVirtualItemFrame CreateVirtualItemFrame(int localX, int localY, int itemType, int stack, byte prefix = 0) => Read(() =>
            {
                if (!sceneRuntime.SupportsVirtualItemFrames) throw new NotSupportedException("Virtual item frames are unavailable in this host.");
                var frame = new SceneVirtualItemFrameDefinition(VirtualItemFrameId.New(), localX, localY, itemType, stack, prefix, 1);
                SetItemFrames([.. definition.VirtualItemFrames, frame]);
                return new VirtualItemFrame(this, frame.Id);
            });

            private void SetItemFrames(SceneVirtualItemFrameDefinition[] frames)
            {
                var next = definition with { VirtualItemFrames = frames.Length == 0 ? Array.Empty<SceneVirtualItemFrameDefinition>() : Array.AsReadOnly(frames) };
                Require(Scene.UpdateOverlay(Id, next));
                definition = next;
            }

            private SceneVirtualItemFrameDefinition ReadItemFrame(VirtualItemFrameId id) => Read(() =>
                definition.VirtualItemFrames.FirstOrDefault(frame => frame.Id == id) ?? throw new ObjectDisposedException($"Virtual item frame {id.Value}"));

            private bool ReplaceItemFrame(VirtualItemFrameId id, long expectedRevision, int itemType, int stack, byte prefix) => Read(() =>
            {
                var current = definition.VirtualItemFrames.FirstOrDefault(frame => frame.Id == id);
                if (current is null || current.Revision != expectedRevision) return false;
                var next = new SceneVirtualItemFrameDefinition(id, current.LocalX, current.LocalY, itemType, stack, prefix, checked(current.Revision + 1));
                SetItemFrames(definition.VirtualItemFrames.Select(frame => frame.Id == id ? next : frame).ToArray());
                return true;
            });

            private void RemoveItemFrame(VirtualItemFrameId id) => Mutate(() =>
            {
                if (definition.VirtualItemFrames.Any(frame => frame.Id == id))
                    SetItemFrames(definition.VirtualItemFrames.Where(frame => frame.Id != id).ToArray());
            });

            private sealed class VirtualItemFrame(TileProvider provider, VirtualItemFrameId id) : IPluginVirtualItemFrame
            {
                private int disposed;
                public VirtualItemFrameId Id => id;
                public SceneVirtualItemFrameDefinition Definition { get { AssertOpen(); return provider.ReadItemFrame(id); } }
                public bool TryReplace(long expectedRevision, int itemType, int stack, byte prefix = 0)
                {
                    AssertOpen();
                    return provider.ReplaceItemFrame(id, expectedRevision, itemType, stack, prefix);
                }
                public void Dispose()
                {
                    if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                    try { provider.RemoveItemFrame(id); }
                    catch (ObjectDisposedException) { /* The owning provider was already removed. */ }
                }
                private void AssertOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            }
        }
    }
}
