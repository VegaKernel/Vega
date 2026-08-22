namespace Vega.Contracts.Worlds;

public sealed record WorldResetPlan(
    Guid OperationId,
    WorldId WorldId,
    string CurrentWorldPath,
    string ReplacementSourcePath,
    string RequestedBy,
    DateTimeOffset RequestedAt);

public enum WorldResetState
{
    Idle,
    Validating,
    Preparing,
    Committing,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}

public sealed record WorldResetStatus(
    Guid? OperationId,
    WorldId? WorldId,
    WorldResetState State,
    DateTimeOffset ChangedAt,
    string? Detail = null)
{
    public static WorldResetStatus Idle { get; } = new(
        null,
        null,
        WorldResetState.Idle,
        DateTimeOffset.MinValue);
}

public sealed record WorldResetResult(
    bool Succeeded,
    WorldResetStatus Status,
    string? ErrorCode = null);

public interface IPreparedWorldReset : IAsyncDisposable
{
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

public interface IWorldResetParticipant
{
    string Name { get; }

    ValueTask ValidateAsync(WorldResetPlan plan, CancellationToken cancellationToken = default);

    ValueTask<IPreparedWorldReset> PrepareAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default);
}

public interface IWorldResetBackend : IWorldResetParticipant
{
}

public interface IWorldResetOrchestrator
{
    WorldResetStatus Read();

    ValueTask<WorldResetResult> ExecuteAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default);

    bool Cancel(Guid operationId);
}
