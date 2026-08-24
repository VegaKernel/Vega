using TerrariaRuntime;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Applies Vega event decisions to Terraria world hooks. The event pipeline decides;
/// this adapter alone owns the final mutation of TerrariaRuntime hook state.
/// </summary>
internal sealed class NativeWorldDecisionHooks : IDisposable
{
    private readonly IEventPipeline<WorldHardmodeStarting> hardmodeStarting;
    private bool attached;

    public NativeWorldDecisionHooks(IEventPipeline<WorldHardmodeStarting> hardmodeStarting)
    {
        this.hardmodeStarting = hardmodeStarting ?? throw new ArgumentNullException(nameof(hardmodeStarting));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        HookEvents.Terraria.WorldGen.StartHardmode += OnStartHardmode;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        HookEvents.Terraria.WorldGen.StartHardmode -= OnStartHardmode;
        attached = false;
    }

    private void OnStartHardmode(
        object? sender,
        HookEvents.Terraria.WorldGen.StartHardmodeEventArgs args)
    {
        if (!args.ContinueExecution)
        {
            return;
        }

        var outcome = hardmodeStarting.Dispatch(new WorldHardmodeStarting());
        if (outcome.IsCancelled)
        {
            args.ContinueExecution = false;
        }
    }
}
