using NUnit.Framework;
using Vega.Terraria.Threading;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class GameThreadDispatcherTests
{
    [Test]
    public void QueueMetricsReportPendingAndRejectedWorkWithoutDraining()
    {
        using var dispatcher = new TerrariaRuntimeGameThreadDispatcher(2, 1, TimeSpan.FromMilliseconds(5));
        Assert.That(dispatcher.TryPost("first", () => { }), Is.True);
        Assert.That(dispatcher.TryPost("second", () => { }), Is.True);
        Assert.That(dispatcher.TryPost("third", () => { }), Is.False);
        var before = dispatcher.ReadQueueMetrics();
        Assert.Multiple(() =>
        {
            Assert.That(before.Pending, Is.EqualTo(2));
            Assert.That(before.Capacity, Is.EqualTo(2));
            Assert.That(before.Dropped, Is.EqualTo(1));
            Assert.That(before.MaximumStepsPerTick, Is.EqualTo(1));
            Assert.That(before.BudgetMilliseconds, Is.GreaterThanOrEqualTo(5));
        });
        dispatcher.RunTick();
        Assert.That(dispatcher.ReadQueueMetrics().Pending, Is.EqualTo(1));
    }

    [Test]
    public void OneShotAndCooperativeWorkPreserveQueueOrderAndTickBoundary()
    {
        using var dispatcher = new TerrariaRuntimeGameThreadDispatcher(4, 2, TimeSpan.FromSeconds(1));
        var order = new List<string>();
        int calls = 0;
        bool nestedAccepted = false;
        Assert.That(dispatcher.TryPost("first", () =>
        {
            order.Add("first");
            nestedAccepted = dispatcher.TryPost("new", () => order.Add("new"));
        }), Is.True);
        Assert.That(dispatcher.TrySchedule("step", () =>
        {
            order.Add($"step-{++calls}");
            return calls == 2;
        }), Is.True);
        Assert.That(dispatcher.TryPost("last", () => order.Add("last")), Is.True);

        dispatcher.RunTick();
        Assert.That(nestedAccepted, Is.True);
        Assert.That(order, Is.EqualTo(new[] { "first", "step-1" }));
        dispatcher.RunTick();
        Assert.That(order, Is.EqualTo(new[] { "first", "step-1", "last", "new" }));
        dispatcher.RunTick();
        Assert.That(order, Is.EqualTo(new[] { "first", "step-1", "last", "new", "step-2" }));
    }

    [Test]
    public void EmptyTickCapturesAuthoritativeThreadAndOtherThreadsDoNotDrain()
    {
        using var dispatcher = new TerrariaRuntimeGameThreadDispatcher(4, 4, TimeSpan.FromSeconds(1));
        dispatcher.RunTick();
        Assert.That(dispatcher.IsGameThreadKnown && dispatcher.IsGameThread, Is.True);
        int calls = 0;
        Assert.That(dispatcher.TryPost("one-shot", () => calls++), Is.True);
        Task.Run(dispatcher.RunTick).GetAwaiter().GetResult();
        Assert.That(calls, Is.Zero);
        dispatcher.RunTick();
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void ConcurrentProducerActionsRemainOneShotAndOrderedWithinEachProducer()
    {
        const int producers = 4, actionsPerProducer = 32;
        using var dispatcher = new TerrariaRuntimeGameThreadDispatcher(producers * actionsPerProducer, 16, TimeSpan.FromSeconds(1));
        var observed = new List<(int Producer, int Index)>();
        bool[] accepted = new bool[producers * actionsPerProducer];
        Parallel.For(0, producers, producer =>
        {
            for (int index = 0; index < actionsPerProducer; index++)
            {
                int captured = index;
                accepted[producer * actionsPerProducer + index] = dispatcher.TryPost("normal-work", () => observed.Add((producer, captured)));
            }
        });
        Assert.That(accepted.All(value => value), Is.True);
        for (int tick = 0; tick < (producers * actionsPerProducer) / 16; tick++) dispatcher.RunTick();
        Assert.That(observed.Count, Is.EqualTo(producers * actionsPerProducer));
        for (int producer = 0; producer < producers; producer++)
            Assert.That(observed.Where(work => work.Producer == producer).Select(work => work.Index),
                Is.EqualTo(Enumerable.Range(0, actionsPerProducer)));
        dispatcher.RunTick();
        Assert.That(observed.Count, Is.EqualTo(producers * actionsPerProducer));
    }
}
