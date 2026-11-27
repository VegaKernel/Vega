using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        public bool SupportsVirtualButtons { get { lock (sync) return !disposed && Services.Scenes is ISceneButtonRuntime; } }

        private sealed partial class TileProvider
        {
            public void SetButtonTargets(IReadOnlyList<SceneButtonTarget> targets)
            {
                ArgumentNullException.ThrowIfNull(targets);
                var selected = targets.ToArray();
                Mutate(() =>
                {
                    if (sceneRuntime is not ISceneButtonRuntime buttons) throw new NotSupportedException("Virtual buttons are unavailable in this host.");
                    Require(buttons.SetButtonTargets(Scene.Id, Id, selected));
                });
            }

            public bool TryTakeButtonPress(out SceneButtonPress? press)
            {
                press = Read(() =>
                {
                    if (sceneRuntime is not ISceneButtonRuntime buttons) return null;
                    while (buttons.TryTakeButtonPress(Scene.Id, Id, out var candidate) && candidate is not null)
                        if (Owner.TryGetEditor(candidate.Observer.Player, out var player) && player is not null &&
                            buttons.IsCurrentButtonPress(player, candidate)) return candidate;
                    return null;
                });
                return press is not null;
            }
        }
    }
}
