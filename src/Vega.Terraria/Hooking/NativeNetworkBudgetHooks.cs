using TerrariaRuntime;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>Bounds admission and per-slot frame work without replacing the vanilla packet pass.</summary>
internal sealed class NativeNetworkBudgetHooks(ServerNetworkOptions options) : IDisposable
{
    private readonly ServerNetworkOptions limits = ServerNetworkOptions.Normalize(options);
    private int previousPendingConnectionLimit;
    private Func<int, int>? previousFrameBudget;
    private bool attached;

    public void Attach()
    {
        if (attached) return;
        previousPendingConnectionLimit = Hooks.Netplay.ServerPendingConnectionLimit;
        previousFrameBudget = Hooks.NetMessage.ServerFrameBudget;
        Hooks.Netplay.ServerPendingConnectionLimit = limits.PendingConnectionLimit;
        Hooks.NetMessage.ServerFrameBudget = GetFrameBudget;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached) return;
        Hooks.Netplay.ServerPendingConnectionLimit = previousPendingConnectionLimit;
        Hooks.NetMessage.ServerFrameBudget = previousFrameBudget;
        attached = false;
    }

    private int GetFrameBudget(int _) => limits.FramesPerSlotPerPass;
}
