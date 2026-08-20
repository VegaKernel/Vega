using OTAPI;
using Vega.Contracts.Runtime;
using HookWorldFile = HookEvents.Terraria.IO.WorldFile;
using HookWorldGen = HookEvents.Terraria.WorldGen;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Vega-owned OTAPI lifecycle hooks. These hooks observe Terraria directly and do not
/// depend on TSAPI server types or the legacy hook manager.
/// </summary>
internal sealed class NativeLifecycleHooks : IDisposable
{
    private readonly IRuntimeLifecycleController lifecycle;
    private bool attached;

    public NativeLifecycleHooks(IRuntimeLifecycleController lifecycle)
    {
        this.lifecycle = lifecycle;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        HookWorldFile.LoadWorld += OnWorldLoadStarted;
        HookWorldGen.GenerateWorld += OnWorldGenerationStarted;
        HookEvents.Terraria.Netplay.StartServer += OnStartServer;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        HookWorldFile.LoadWorld -= OnWorldLoadStarted;
        HookWorldGen.GenerateWorld -= OnWorldGenerationStarted;
        HookEvents.Terraria.Netplay.StartServer -= OnStartServer;
        attached = false;
    }

    private void OnWorldLoadStarted(object? sender, HookWorldFile.LoadWorldEventArgs args) =>
        AdvanceToWorldLoading();

    private void OnWorldGenerationStarted(object? sender, HookWorldGen.GenerateWorldEventArgs args) =>
        AdvanceToWorldLoading();

    private void AdvanceToWorldLoading()
    {
        if (lifecycle.CurrentStage == RuntimeLifecycleStage.ModulesRegistered)
        {
            lifecycle.Advance(RuntimeLifecycleStage.WorldLoading);
        }
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
