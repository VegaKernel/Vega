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
        Assert.That(new ServerNetworkOptions().UseOrderedTcp, Is.True);
        Assert.That(new ServerNetworkOptions().OrderedDeferredWake, Is.True);
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedDeferredWakeMilliseconds = 99 }).OrderedDeferredWakeMilliseconds, Is.EqualTo(2));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedDeferredWakeMilliseconds = double.NaN }).OrderedDeferredWakeMilliseconds, Is.EqualTo(2));
        Assert.That(new ServerNetworkOptions().OrderedCallbacksPerPass, Is.EqualTo(8192));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCallbacksPerPass = int.MaxValue }).OrderedCallbacksPerPass, Is.EqualTo(8192));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCallbacksPerPass = 1 }).OrderedCallbacksPerPass, Is.EqualTo(1));
        Assert.That(new NativeTcpTransportOptions().CallbackBudget, Is.EqualTo(TimeSpan.FromMilliseconds(2)));
        Assert.That(new ServerNetworkOptions().OrderedCompletionDrainBatch, Is.EqualTo(4));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCompletionDrainBatch = 0 }).OrderedCompletionDrainBatch, Is.EqualTo(1));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedCompletionDrainBatch = int.MaxValue }).OrderedCompletionDrainBatch, Is.EqualTo(4));
        Assert.That(new NativeTcpTransportOptions().SingleCancellationWake, Is.True);
        Assert.That(new ServerNetworkOptions().OrderedBatchRequests, Is.EqualTo(16));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedBatchRequests = int.MaxValue }).OrderedBatchRequests, Is.EqualTo(16));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedBatchRequests = 0 }).OrderedBatchRequests, Is.EqualTo(1));
        Assert.That(ServerNetworkOptions.Normalize(new() { OrderedBatchRequests = 4 }).OrderedBatchRequests, Is.EqualTo(4));
    }
    [Test]
    public void ProducerBatchingRequiresOrderedDeferredWakeAndMoreThanOneRequest()
    {
        var enabled = new ServerNetworkOptions();
        Assert.That(enabled.DistantMovementUpdatesPerSecond, Is.EqualTo(5));
        Assert.That(enabled.OrderedDeferredWakeMilliseconds, Is.EqualTo(2));
        Assert.That(ServerNetworkOptions.Normalize(enabled with { OrderedProducerBatching = false }).OrderedProducerBatching, Is.False);
        Assert.That(ServerNetworkOptions.Normalize(enabled).OrderedProducerBatching, Is.True);
        Assert.That(ServerNetworkOptions.Normalize(enabled with { UseOrderedTcp = false }).OrderedProducerBatching, Is.False);
        Assert.That(ServerNetworkOptions.Normalize(enabled with { OrderedDeferredWake = false }).OrderedProducerBatching, Is.False);
        Assert.That(ServerNetworkOptions.Normalize(enabled with { OrderedBatchRequests = 1 }).OrderedProducerBatching, Is.False);
        Assert.That(() => new NativeTcpTransportOptions { ProducerBatching = true, MaxBatchRequests = 16 }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new NativeTcpTransportOptions { ProducerBatching = true, DeferWriterWake = true }.Validate(), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new NativeTcpTransportOptions { ProducerBatching = true, DeferWriterWake = true, MaxBatchRequests = 16 }.Validate(), Throws.Nothing);
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
