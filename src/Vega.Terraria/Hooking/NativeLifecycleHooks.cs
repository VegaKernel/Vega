using OTAPI;
using Vega.Contracts.Runtime;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Vega-owned OTAPI lifecycle hooks. These hooks observe Terraria directly and do not
/// depend on TSAPI server types or the legacy hook manager.
/// </summary>
internal sealed class NativeLifecycleHooks : IDisposable
{
    private readonly RuntimeLifecycleTracker lifecycle;
    private bool attached;

    public NativeLifecycleHooks(RuntimeLifecycleTracker lifecycle)
    {
        this.lifecycle = lifecycle;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        HookEvents.Terraria.Netplay.StartServer += OnStartServer;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        HookEvents.Terraria.Netplay.StartServer -= OnStartServer;
        attached = false;
    }

    private void OnStartServer(object? sender, HookEvents.Terraria.Netplay.StartServerEventArgs args)
    {
        if (!args.ContinueExecution || lifecycle.CurrentStage != RuntimeLifecycleStage.WorldLoading)
        {
            return;
        }

        lifecycle.Advance(RuntimeLifecycleStage.WorldReady);
        lifecycle.Advance(RuntimeLifecycleStage.NetworkReady);

        // Vega has no native warmup tasks yet. These transitions become asynchronous
        // once the module startup pipeline introduced by ADR 0017 is implemented.
        lifecycle.Advance(RuntimeLifecycleStage.BackgroundWarmup);
        lifecycle.Advance(RuntimeLifecycleStage.FullyWarmed);
    }
}
