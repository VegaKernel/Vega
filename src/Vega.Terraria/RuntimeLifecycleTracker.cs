using System.Diagnostics;
using Vega.Contracts.Runtime;

namespace Vega.Terraria;

internal sealed class RuntimeLifecycleTracker : IRuntimeLifecycle
{
    private readonly object sync = new();
    private readonly long processStartedAt;
    private long stageStartedAt;
    private RuntimeLifecycleStage currentStage;

    public RuntimeLifecycleTracker(long processStartedAt)
    {
        this.processStartedAt = processStartedAt;
        stageStartedAt = processStartedAt;
        currentStage = RuntimeLifecycleStage.ProcessStart;
    }

    public RuntimeLifecycleStage CurrentStage
    {
        get
        {
            lock (sync)
            {
                return currentStage;
            }
        }
    }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(processStartedAt);

    public event Action<RuntimeLifecycleTransition>? Transitioned;

    public void Advance(RuntimeLifecycleStage nextStage)
    {
        RuntimeLifecycleTransition transition;

        lock (sync)
        {
            if (nextStage <= currentStage)
            {
                return;
            }

            if ((int)nextStage != (int)currentStage + 1)
            {
                throw new InvalidOperationException(
                    $"Invalid Vega lifecycle transition {currentStage} -> {nextStage}.");
            }

            var now = Stopwatch.GetTimestamp();
            transition = new RuntimeLifecycleTransition(
                nextStage,
                Stopwatch.GetElapsedTime(processStartedAt, now),
                Stopwatch.GetElapsedTime(stageStartedAt, now));

            currentStage = nextStage;
            stageStartedAt = now;
        }

        Transitioned?.Invoke(transition);
    }
}
