using System.Collections.Concurrent;
using System.Diagnostics;
using OTAPI;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Threading;

/// <summary>
/// Bounded, time-sliced game-thread scheduler driven directly by the Terraria update hook.
/// </summary>
internal sealed class OtapiGameThreadDispatcher : IGameThreadDispatcher, IDisposable
{
    private readonly record struct WorkItem(string Name, Func<bool> Step);

    private readonly ConcurrentQueue<WorkItem> pending = new();
    private readonly int queueCapacity;
    private readonly int maxStepsPerTick;
    private readonly long budgetTicks;
    private int pendingCount;
    private int droppedCount;
    private int gameThreadId;
    private bool attached;

    public OtapiGameThreadDispatcher(
        int queueCapacity = 4096,
        int maxStepsPerTick = 128,
        TimeSpan? budget = null)
    {
        if (queueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        }

        if (maxStepsPerTick <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxStepsPerTick));
        }

        var effectiveBudget = budget ?? TimeSpan.FromMilliseconds(2);
        if (effectiveBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }

        this.queueCapacity = queueCapacity;
        this.maxStepsPerTick = maxStepsPerTick;
        budgetTicks = Math.Max(1, (long)Math.Ceiling(effectiveBudget.TotalSeconds * Stopwatch.Frequency));
    }

    public bool IsGameThreadKnown => Volatile.Read(ref gameThreadId) != 0;

    public bool IsGameThread
    {
        get
        {
            var captured = Volatile.Read(ref gameThreadId);
            return captured != 0 && captured == Environment.CurrentManagedThreadId;
        }
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        HookEvents.Terraria.Main.Update += OnUpdate;
        attached = true;
    }

    public void AssertGameThread()
    {
        var captured = Volatile.Read(ref gameThreadId);
        if (captured == 0)
        {
            throw new InvalidOperationException("The Terraria game thread has not been identified yet.");
        }

        if (captured != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                $"Terraria state must be accessed on game thread {captured}; current thread is {Environment.CurrentManagedThreadId}.");
        }
    }

    public bool TryPost(string name, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return TrySchedule(name, () =>
        {
            action();
            return true;
        });
    }

    public bool TrySchedule(string name, Func<bool> step)
    {
        ArgumentNullException.ThrowIfNull(step);

        var size = Interlocked.Increment(ref pendingCount);
        if (size <= queueCapacity)
        {
            pending.Enqueue(new WorkItem(NormalizeName(name), step));
            return true;
        }

        Interlocked.Decrement(ref pendingCount);
        var dropped = Interlocked.Increment(ref droppedCount);
        if ((dropped & 0xFF) == 1)
        {
            Console.Error.WriteLine(
                $"Vega game-thread queue is full. dropped={dropped}, capacity={queueCapacity}.");
        }

        return false;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        HookEvents.Terraria.Main.Update -= OnUpdate;
        attached = false;
    }

    private void OnUpdate(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args)
    {
        CaptureGameThread();
        Drain();
    }

    private void CaptureGameThread()
    {
        var current = Environment.CurrentManagedThreadId;
        var captured = Interlocked.CompareExchange(ref gameThreadId, current, 0);

        if (captured != 0 && captured != current)
        {
            throw new InvalidOperationException(
                $"Terraria game-thread identity changed from {captured} to {current}.");
        }
    }

    private void Drain()
    {
        AssertGameThread();

        var startedAt = Stopwatch.GetTimestamp();
        var processed = 0;

        while (processed < maxStepsPerTick &&
               Stopwatch.GetTimestamp() - startedAt < budgetTicks &&
               pending.TryDequeue(out var item))
        {
            var completed = true;
            try
            {
                completed = item.Step();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Vega game-thread work '{item.Name}' failed: {ex}");
            }

            processed++;

            if (completed)
            {
                Interlocked.Decrement(ref pendingCount);
            }
            else
            {
                pending.Enqueue(item);
            }
        }
    }

    private static string NormalizeName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
}
