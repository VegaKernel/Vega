using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Core.Worlds;

public sealed class WorldResetOrchestrator : IWorldResetOrchestrator, IDisposable
{
    private readonly IWorldResetParticipant[] participants;
    private readonly IOperationsLogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private WorldResetStatus status = WorldResetStatus.Idle;
    private CancellationTokenSource? active;

    public WorldResetOrchestrator(
        IWorldResetBackend backend,
        IEnumerable<IWorldResetParticipant>? participants,
        IOperationsLogger logger)
    {
        this.participants = [
            .. participants ?? [],
            backend ?? throw new ArgumentNullException(nameof(backend))
        ];
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WorldResetStatus Read() => Volatile.Read(ref status);

    public async ValueTask<WorldResetResult> ExecuteAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new WorldResetResult(false, Read(), "world-reset.busy");
        }

        var prepared = new List<IPreparedWorldReset>(participants.Length);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (sync)
        {
            active = linked;
        }

        try
        {
            ValidatePlan(plan);
            SetStatus(plan, WorldResetState.Validating);
            foreach (IWorldResetParticipant participant in participants)
            {
                await participant.ValidateAsync(plan, linked.Token).ConfigureAwait(false);
            }

            SetStatus(plan, WorldResetState.Preparing);
            foreach (IWorldResetParticipant participant in participants)
            {
                prepared.Add(await participant.PrepareAsync(plan, linked.Token).ConfigureAwait(false));
            }

            SetStatus(plan, WorldResetState.Committing);
            foreach (IPreparedWorldReset operation in prepared)
            {
                await operation.CommitAsync(linked.Token).ConfigureAwait(false);
            }

            SetStatus(plan, WorldResetState.Completed);
            logger.Log(
                OperationsLogLevel.Warning,
                "world-reset",
                $"World reset {plan.OperationId} committed for {plan.WorldId} by {plan.RequestedBy}.");
            return new WorldResetResult(true, Read());
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            SetStatus(plan, WorldResetState.Cancelling);
            await RollbackAsync(prepared).ConfigureAwait(false);
            SetStatus(plan, WorldResetState.Cancelled);
            return new WorldResetResult(false, Read(), "world-reset.cancelled");
        }
        catch (Exception ex)
        {
            await RollbackAsync(prepared).ConfigureAwait(false);
            SetStatus(plan, WorldResetState.Failed, ex.Message);
            logger.Log(
                OperationsLogLevel.Error,
                "world-reset",
                $"World reset {plan.OperationId} failed for {plan.WorldId}.",
                ex);
            return new WorldResetResult(false, Read(), "world-reset.failed");
        }
        finally
        {
            foreach (IPreparedWorldReset operation in prepared)
            {
                await operation.DisposeAsync().ConfigureAwait(false);
            }

            lock (sync)
            {
                active = null;
            }

            gate.Release();
        }
    }

    public bool Cancel(Guid operationId)
    {
        lock (sync)
        {
            if (Read().OperationId != operationId || active is null)
            {
                return false;
            }

            active.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            active?.Cancel();
        }

        gate.Dispose();
    }

    private static void ValidatePlan(WorldResetPlan plan)
    {
        if (plan.OperationId == Guid.Empty || plan.WorldId.Value.Length == 0 ||
            string.IsNullOrWhiteSpace(plan.CurrentWorldPath) ||
            string.IsNullOrWhiteSpace(plan.ReplacementSourcePath) ||
            string.IsNullOrWhiteSpace(plan.RequestedBy))
        {
            throw new ArgumentException("World reset plan is incomplete.", nameof(plan));
        }

        string current = Path.GetFullPath(plan.CurrentWorldPath);
        string replacement = Path.GetFullPath(plan.ReplacementSourcePath);
        if (string.Equals(current, replacement, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Replacement source must differ from the active world.", nameof(plan));
        }
    }

    private async ValueTask RollbackAsync(List<IPreparedWorldReset> prepared)
    {
        for (var index = prepared.Count - 1; index >= 0; index--)
        {
            try
            {
                await prepared[index].RollbackAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Log(OperationsLogLevel.Critical, "world-reset", "World reset rollback failed.", ex);
            }
        }
    }

    private void SetStatus(
        WorldResetPlan plan,
        WorldResetState state,
        string? detail = null) =>
        Volatile.Write(
            ref status,
            new WorldResetStatus(
                plan.OperationId,
                plan.WorldId,
                state,
                DateTimeOffset.UtcNow,
                detail));
}
