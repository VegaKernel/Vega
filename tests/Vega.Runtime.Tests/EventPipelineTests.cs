using NUnit.Framework;
using Vega.Contracts.Events;
using Vega.Core.Events;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class EventPipelineTests
{
    [Test]
    public void Dispatch_orders_handlers_by_priority_then_registration_order()
    {
        var calls = new List<string>();
        var pipeline = new EventPipeline<TestEvent>(
        [
            new Handler(EventPriority.Late, false, _ => calls.Add("late")),
            new Handler(EventPriority.Early, false, _ => calls.Add("early")),
            new Handler(EventPriority.Normal, false, _ => calls.Add("normal-1")),
            new Handler(EventPriority.Normal, false, _ => calls.Add("normal-2"))
        ]);

        EventOutcome<TestEvent> outcome = pipeline.Dispatch(new TestEvent(42));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.IsCancelled, Is.False);
            Assert.That(calls, Is.EqualTo(new[] { "early", "normal-1", "normal-2", "late" }));
        });
    }

    [Test]
    public void Dispatch_skips_cancelled_handlers_preserves_first_reason_and_monitors_final_outcome()
    {
        var calls = new List<string>();
        bool secondCancellationAccepted = true;
        EventOutcome<TestEvent>? observed = null;

        var pipeline = new EventPipeline<TestEvent>(
        [
            new Handler(EventPriority.Early, false, context =>
            {
                calls.Add("cancel");
                context.Cancel(new EventCancellation("world.hardmode.blocked", "Hardmode is disabled.", "test"));
            }),
            new Handler(EventPriority.Normal, false, _ => calls.Add("must-not-run")),
            new Handler(EventPriority.Late, true, context =>
            {
                calls.Add("cancel-aware");
                secondCancellationAccepted = context.Cancel(new EventCancellation("replacement"));
            })
        ],
        [
            new Monitor(EventPriority.Normal, outcome =>
            {
                calls.Add("monitor");
                observed = outcome;
            })
        ]);

        EventOutcome<TestEvent> result = pipeline.Dispatch(new TestEvent(7));

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(new[] { "cancel", "cancel-aware", "monitor" }));
            Assert.That(secondCancellationAccepted, Is.False);
            Assert.That(result.IsCancelled, Is.True);
            Assert.That(result.Cancellation?.Code, Is.EqualTo("world.hardmode.blocked"));
            Assert.That(observed, Is.EqualTo(result));
        });
    }

    [Test]
    public async Task DispatchAsync_uses_the_async_handler_contract_and_honors_cancellation()
    {
        var calls = new List<string>();
        var pipeline = new AsyncEventPipeline<TestEvent>(
        [
            new AsyncHandler(EventPriority.Early, false, (context, _) =>
            {
                calls.Add("async-cancel");
                context.Cancel(new EventCancellation("async.blocked"));
                return ValueTask.CompletedTask;
            }),
            new AsyncHandler(EventPriority.Normal, false, (_, _) =>
            {
                calls.Add("must-not-run");
                return ValueTask.CompletedTask;
            }),
            new AsyncHandler(EventPriority.Late, true, (_, _) =>
            {
                calls.Add("async-cancel-aware");
                return ValueTask.CompletedTask;
            })
        ],
        [
            new AsyncMonitor(EventPriority.Normal, (outcome, _) =>
            {
                calls.Add($"monitor:{outcome.Cancellation?.Code}");
                return ValueTask.CompletedTask;
            })
        ]);

        EventOutcome<TestEvent> result = await pipeline.DispatchAsync(new TestEvent(11));

        Assert.Multiple(() =>
        {
            Assert.That(result.Cancellation?.Code, Is.EqualTo("async.blocked"));
            Assert.That(calls, Is.EqualTo(new[] { "async-cancel", "async-cancel-aware", "monitor:async.blocked" }));
        });
    }

    private readonly record struct TestEvent(int Value);

    private sealed class Handler : IEventHandler<TestEvent>
    {
        private readonly Action<EventContext<TestEvent>> action;

        public Handler(EventPriority priority, bool invokeWhenCancelled, Action<EventContext<TestEvent>> action)
        {
            Priority = priority;
            InvokeWhenCancelled = invokeWhenCancelled;
            this.action = action;
        }

        public EventPriority Priority { get; }

        public bool InvokeWhenCancelled { get; }

        public void Handle(EventContext<TestEvent> context) => action(context);
    }

    private sealed class Monitor : IEventMonitor<TestEvent>
    {
        private readonly Action<EventOutcome<TestEvent>> action;

        public Monitor(EventPriority priority, Action<EventOutcome<TestEvent>> action)
        {
            Priority = priority;
            this.action = action;
        }

        public EventPriority Priority { get; }

        public void Observe(EventOutcome<TestEvent> outcome) => action(outcome);
    }

    private sealed class AsyncHandler : IAsyncEventHandler<TestEvent>
    {
        private readonly Func<EventContext<TestEvent>, CancellationToken, ValueTask> action;

        public AsyncHandler(
            EventPriority priority,
            bool invokeWhenCancelled,
            Func<EventContext<TestEvent>, CancellationToken, ValueTask> action)
        {
            Priority = priority;
            InvokeWhenCancelled = invokeWhenCancelled;
            this.action = action;
        }

        public EventPriority Priority { get; }

        public bool InvokeWhenCancelled { get; }

        public ValueTask HandleAsync(EventContext<TestEvent> context, CancellationToken cancellationToken) =>
            action(context, cancellationToken);
    }

    private sealed class AsyncMonitor : IAsyncEventMonitor<TestEvent>
    {
        private readonly Func<EventOutcome<TestEvent>, CancellationToken, ValueTask> action;

        public AsyncMonitor(
            EventPriority priority,
            Func<EventOutcome<TestEvent>, CancellationToken, ValueTask> action)
        {
            Priority = priority;
            this.action = action;
        }

        public EventPriority Priority { get; }

        public ValueTask ObserveAsync(EventOutcome<TestEvent> outcome, CancellationToken cancellationToken) =>
            action(outcome, cancellationToken);
    }
}
