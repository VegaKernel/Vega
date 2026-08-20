namespace Vega.Contracts.Worlds;

public readonly record struct WorldClockValue(bool IsDaytime, double TimeTicks);

public enum WorldClockAction
{
    None,
    Correct,
    Reanchor
}

public readonly record struct WorldClockDecision(
    WorldClockAction Action,
    WorldClockValue Target,
    double DriftTicks);

public sealed record WorldClockSnapshot(
    WorldId WorldId,
    bool Enabled,
    bool IsAnchored,
    WorldClockValue Observed,
    WorldClockValue Predicted,
    double DriftTicks,
    long CorrectionCount,
    long ReanchorCount,
    DateTimeOffset SampledAt);

public interface IWorldClockReader
{
    IReadOnlyList<WorldClockSnapshot> SnapshotWorldClocks();
}

public interface IWorldClockController : IWorldClockReader
{
    bool Enabled { get; }

    WorldClockDecision Observe(WorldId worldId, WorldClockValue observed);

    void Reset(WorldId worldId);
}
