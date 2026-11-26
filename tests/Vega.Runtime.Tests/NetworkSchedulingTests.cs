using NUnit.Framework;
using TerrariaRuntime;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;
using Vega.Terraria.Threading;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NetworkSchedulingTests
{
    [Test]
    public void OrderedCompletionCeilingIsProductionSizedButBoundedIndependentlyOfTimeBudget()
    {
        Assert.That(new ServerNetworkOptions().UseOrderedTcp, Is.False);
        Assert.That(new ServerNetworkOptions().OrderedCallbacksPerPass, Is.EqualTo(8192));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCallbacksPerPass = int.MaxValue }).OrderedCallbacksPerPass, Is.EqualTo(8192));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCallbacksPerPass = 1 }).OrderedCallbacksPerPass, Is.EqualTo(1));
        Assert.That(new NativeTcpTransportOptions().CallbackBudget, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
    }
    [Test]
    public void YieldingWorkRunsAtMostOncePerTickAndRetainsItsQueueReservation()
    {
        using var dispatcher = new TerrariaRuntimeGameThreadDispatcher(1, 128, TimeSpan.FromSeconds(1));
        int calls = 0;
        Assert.That(dispatcher.TrySchedule("cooperative", () => ++calls == 2), Is.True);
        dispatcher.RunTick();
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(dispatcher.TryPost("overflow", () => { }), Is.False);
        dispatcher.RunTick();
        Assert.That(calls, Is.EqualTo(2));
        Assert.That(dispatcher.TryPost("next", () => { }), Is.True);
    }

    [Test]
    [NonParallelizable]
    public void NetworkBudgetHooksApplyBoundedLimitsAndRestorePreviousValues()
    {
        int pending = Hooks.Netplay.ServerPendingConnectionLimit;
        var budget = Hooks.NetMessage.ServerFrameBudget;
        using (var hooks = new NativeNetworkBudgetHooks(new ServerNetworkOptions
               { PendingConnectionLimit = 0, FramesPerSlotPerPass = int.MaxValue }))
        {
            hooks.Attach();
            Assert.That(Hooks.Netplay.ServerPendingConnectionLimit, Is.EqualTo(1));
            Assert.That(Hooks.NetMessage.ServerFrameBudget!(5), Is.EqualTo(256));
        }
        Assert.That(Hooks.Netplay.ServerPendingConnectionLimit, Is.EqualTo(pending));
        Assert.That(Hooks.NetMessage.ServerFrameBudget, Is.SameAs(budget));
    }
}
