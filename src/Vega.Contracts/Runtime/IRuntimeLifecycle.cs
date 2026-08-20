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

/// <summary>
/// Advances the runtime lifecycle while keeping read-only consumers on <see cref="IRuntimeLifecycle"/>.
/// </summary>
public interface IRuntimeLifecycleController : IRuntimeLifecycle
{
    void Advance(RuntimeLifecycleStage nextStage);
}

public readonly record struct RuntimeLifecycleTransition(
    RuntimeLifecycleStage Stage,
    TimeSpan Elapsed,
    TimeSpan StageDuration);
