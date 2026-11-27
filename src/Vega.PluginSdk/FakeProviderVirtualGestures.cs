using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        public bool SupportsVirtualGestures { get { lock (sync) return !disposed && Services.Scenes is ISceneGestureRuntime; } }

        private sealed partial class TileProvider
        {
            public void SetGestureTargets(IReadOnlyList<SceneGestureTarget> targets)
            {
                ArgumentNullException.ThrowIfNull(targets);
                var selected = targets.ToArray();
                Mutate(() =>
                {
                    if (sceneRuntime is not ISceneGestureRuntime gestures) throw new NotSupportedException("Virtual gestures are unavailable in this host.");
                    Require(gestures.SetGestureTargets(Scene.Id, Id, selected));
                });
            }

            public bool TryTakeGesture(out SceneGestureEvent? input)
            {
                input = Read(() =>
                {
                    if (sceneRuntime is not ISceneGestureRuntime gestures) return null;
                    while (gestures.TryTakeGesture(Scene.Id, Id, out var candidate) && candidate is not null)
                        if (Owner.TryGetEditor(candidate.Observer.Player, out var player) && player is not null &&
                            gestures.IsCurrentGesture(player, candidate)) return candidate;
                    return null;
                });
                return input is not null;
            }
        }
    }
}
