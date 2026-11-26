using System.Collections.Concurrent;
using System.Diagnostics;
using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Threading;

/// <summary>
/// Bounded, time-sliced game-thread scheduler driven directly by the Terraria update hook.
/// </summary>
public sealed class TerrariaRuntimeGameThreadDispatcher : IGameThreadDispatcher, IDisposable
{
    private readonly record struct WorkItem(string Name, Delegate Callback)
    {
        public bool Step()
        {
            if (Callback is Action action)
            {
                action();
                return true;
            }

            return ((Func<bool>)Callback)();
        }
    }

    private readonly ConcurrentQueue<WorkItem> pending = new();
    private readonly int queueCapacity;
    private readonly int maxStepsPerTick;
    private readonly long budgetTicks;
    private int pendingCount;
    private long droppedCount;
    private int gameThreadId;
    private int threadMismatchReported;
    private bool attached;

    public TerrariaRuntimeGameThreadDispatcher(
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

    public GameThreadQueueSnapshot ReadQueueMetrics() => new(
        Math.Clamp(Volatile.Read(ref pendingCount), 0, queueCapacity), queueCapacity, Volatile.Read(ref droppedCount),
        maxStepsPerTick, budgetTicks * 1000d / Stopwatch.Frequency);

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
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnIdleUpdate;
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
        return TryEnqueue(name, action);
    }

    public bool TrySchedule(string name, Func<bool> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return TryEnqueue(name, step);
    }

    private bool TryEnqueue(string name, Delegate callback)
    {
        var size = Interlocked.Increment(ref pendingCount);
        if (size <= queueCapacity)
        {
            pending.Enqueue(new WorkItem(NormalizeName(name), callback));
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
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnIdleUpdate;
        attached = false;
    }

    private void OnUpdate(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args)
    {
        RunTick();
    }

    private void OnIdleUpdate()
    {
        if (!global::Terraria.Netplay.HasFullyConnectedClients)
        {
            RunTick();
        }
    }

    internal void RunTick()
    {
        if (!TryCaptureGameThread())
        {
            return;
        }

        Drain();
    }

    private bool TryCaptureGameThread()
    {
        int current = Environment.CurrentManagedThreadId;
        int captured = Volatile.Read(ref gameThreadId);
        if (captured == 0)
            captured = Interlocked.CompareExchange(ref gameThreadId, current, 0);
        if (captured == 0 || captured == current)
        {
            return true;
        }

        if (Interlocked.Exchange(ref threadMismatchReported, 1) == 0)
        {
            Console.Error.WriteLine(
                $"Vega ignored a Terraria update on unexpected thread {current}; game thread is {captured}.");
        }

        return false;
    }

    private void Drain()
    {
        AssertGameThread();
        if (Volatile.Read(ref pendingCount) == 0)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var processed = 0;
        // A cooperative step that yields must wait until the next tick. Do not repeatedly
        // invoke it (or immediately consume newly posted work) within the same drain.
        int stepsThisTick = Math.Min(maxStepsPerTick, pending.Count);

        while (processed < stepsThisTick &&
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
