namespace Vega.Contracts.Runtime;

/// <summary>
/// Read-only view of Vega startup/readiness state.
/// </summary>
public interface IRuntimeLifecycle
{
    RuntimeLifecycleStage CurrentStage { get; }

    TimeSpan Elapsed { get; }

    event Action<RuntimeLifecycleTransition>? Transitioned;
}

public readonly record struct RuntimeLifecycleTransition(
    RuntimeLifecycleStage Stage,
    TimeSpan Elapsed,
    TimeSpan StageDuration);
