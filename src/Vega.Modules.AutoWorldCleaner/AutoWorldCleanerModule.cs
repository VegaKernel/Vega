using System.Reflection;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Modules.AutoWorldCleaner;

public enum AutoWorldCleanerRunStatus
{
    Applied,
    Disabled,
    SkippedNotReady,
    SkippedNoPlayers,
    Busy,
    Cancelled,
    Failed
}

public readonly record struct AutoWorldCleanerRunResult(
    AutoWorldCleanerRunStatus Status,
    int RemovedItems = 0,
    int RemovedProjectiles = 0,
    int SkippedItems = 0,
    int SkippedProjectiles = 0,
    string? ErrorCode = null);

public sealed class AutoWorldCleanerModule : IVegaModule, IWorldCleanupStatusReader
{
    private readonly AutoWorldCleanerOptions options;
    private readonly IWorldEntityMaintenance entities;
    private readonly IRuntimeLifecycle lifecycle;
    private readonly IGameThreadDispatcher dispatcher;
    private readonly IPlayerDirectory players;
    private readonly IOperationsLogger logger;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim runGate = new(1, 1);
    private CancellationTokenSource? shutdown;
    private Task? worker;
    private WorldCleanupStatusSnapshot status;
    private bool started;
    private bool disposed;

    public AutoWorldCleanerModule(
        AutoWorldCleanerOptions options,
        IWorldEntityMaintenance entities,
        IRuntimeLifecycle lifecycle,
        IGameThreadDispatcher dispatcher,
        IPlayerDirectory players,
        IOperationsLogger logger,
        TimeProvider? timeProvider = null)
    {
        this.options = AutoWorldCleanerOptions.Normalize(options);
        this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        status = this.options.Enabled
            ? WorldCleanupStatusSnapshot.Disabled with { Enabled = true, State = WorldCleanupRunState.Idle }
            : WorldCleanupStatusSnapshot.Disabled;
    }

    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Modules.AutoWorldCleaner",
        typeof(AutoWorldCleanerModule).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ["Vega.Contracts"]);

    public AutoWorldCleanerOptions Options => options;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            return;
        }

        started = true;
        if (!options.Enabled)
        {
            Volatile.Write(ref status, WorldCleanupStatusSnapshot.Disabled);
            return;
        }

        Volatile.Write(
            ref status,
            WorldCleanupStatusSnapshot.Disabled with
            {
                Enabled = true,
                State = WorldCleanupRunState.Idle
            });
        shutdown = new CancellationTokenSource();
        worker = Task.Run(() => RunScheduleAsync(shutdown.Token));
    }

    public ValueTask<AutoWorldCleanerRunResult> RunNowAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default) =>
        RunNowAsync(worldId, "manual", cancellationToken);

    public WorldCleanupStatusSnapshot Read() => Volatile.Read(ref status);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        shutdown?.Cancel();
        try
        {
            worker?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            shutdown?.Dispose();
        }
    }

    private async Task RunScheduleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.IntervalMinutes),
            timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunNowAsync(WorldId.Primary, "scheduled", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<AutoWorldCleanerRunResult> RunNowAsync(
        WorldId worldId,
        string trigger,
        CancellationToken cancellationToken)
    {
        if (!started || !options.Enabled)
        {
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Disabled);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Cancelled);
        }

        if (lifecycle.CurrentStage < RuntimeLifecycleStage.WorldReady)
        {
            UpdateCompletedStatus(WorldCleanupRunState.Skipped, 0, 0, "world-not-ready");
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.SkippedNotReady);
        }

        if (options.OnlyWhenPlayersOnline && players.SnapshotOnlinePlayers().Count == 0)
        {
            UpdateCompletedStatus(WorldCleanupRunState.Skipped, 0, 0, "no-players");
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.SkippedNoPlayers);
        }

        if (!runGate.Wait(0))
        {
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Busy);
        }

        try
        {
            DateTimeOffset startedAt = timeProvider.GetUtcNow();
            Volatile.Write(
                ref status,
                Read() with
                {
                    State = WorldCleanupRunState.Running,
                    LastStartedAt = startedAt,
                    Detail = trigger
                });

            AutoWorldCleanerRunResult result = await DispatchAsync(worldId, cancellationToken)
                .ConfigureAwait(false);
            RecordResult(result, trigger);
            return result;
        }
        finally
        {
            runGate.Release();
        }
    }

    private ValueTask<AutoWorldCleanerRunResult> DispatchAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Cancelled));
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(new AutoWorldCleanerRunResult(
                AutoWorldCleanerRunStatus.Failed,
                ErrorCode: "auto_world_cleaner.dispatcher_unavailable"));
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(ExecuteOnGameThread(worldId, cancellationToken));
        }

        var completion = new TaskCompletionSource<AutoWorldCleanerRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(
                "auto-world-cleaner.run",
                () => completion.TrySetResult(
                    cancellationToken.IsCancellationRequested
                        ? new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Cancelled)
                        : ExecuteOnGameThread(worldId, cancellationToken))))
        {
            return ValueTask.FromResult(new AutoWorldCleanerRunResult(
                AutoWorldCleanerRunStatus.Failed,
                ErrorCode: "auto_world_cleaner.dispatch_queue_full"));
        }

        return new ValueTask<AutoWorldCleanerRunResult>(completion.Task);
    }

    private AutoWorldCleanerRunResult ExecuteOnGameThread(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorldEntityCleanupSnapshot snapshot = entities.Snapshot(worldId);

            WorldItemCleanupHandle[] items = SelectItems(snapshot);
            WorldProjectileCleanupHandle[] projectiles = SelectProjectiles(snapshot);
            WorldEntityRemovalResult removed = entities.Remove(
                new WorldEntityRemovalRequest(worldId, items, projectiles));

            return new AutoWorldCleanerRunResult(
                AutoWorldCleanerRunStatus.Applied,
                removed.RemovedItems,
                removed.RemovedProjectiles,
                removed.SkippedItems,
                removed.SkippedProjectiles);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AutoWorldCleanerRunResult(AutoWorldCleanerRunStatus.Cancelled);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "auto-world-cleaner",
                $"World cleanup failed for '{worldId}'.",
                ex);
            return new AutoWorldCleanerRunResult(
                AutoWorldCleanerRunStatus.Failed,
                ErrorCode: "auto_world_cleaner.failed");
        }
    }

    private WorldItemCleanupHandle[] SelectItems(WorldEntityCleanupSnapshot snapshot)
    {
        if (!options.Items.Enabled || snapshot.Items.Count < options.Items.MinimumActiveCount)
        {
            return [];
        }

        int minimumAgeTicks = checked(options.Items.MinimumAgeSeconds * 60);
        return snapshot.Items
            .Where(item => item.Handle.SpawnAgeTicks >= minimumAgeTicks)
            .Select(static item => item.Handle)
            .ToArray();
    }

    private WorldProjectileCleanupHandle[] SelectProjectiles(WorldEntityCleanupSnapshot snapshot)
    {
        if (!options.Projectiles.Enabled)
        {
            return [];
        }

        WorldProjectileCleanupSnapshot[] eligible = snapshot.Projectiles
            .Where(projectile =>
                (!options.Projectiles.ExcludeMinions || !projectile.IsMinion) &&
                (!options.Projectiles.ExcludeSentries || !projectile.IsSentry))
            .ToArray();
        return eligible.Length < options.Projectiles.MinimumActiveCount
            ? []
            : eligible.Select(static projectile => projectile.Handle).ToArray();
    }

    private void RecordResult(AutoWorldCleanerRunResult result, string trigger)
    {
        switch (result.Status)
        {
            case AutoWorldCleanerRunStatus.Applied:
                UpdateCompletedStatus(
                    WorldCleanupRunState.Succeeded,
                    result.RemovedItems,
                    result.RemovedProjectiles,
                    trigger);
                logger.Log(
                    OperationsLogLevel.Information,
                    "auto-world-cleaner",
                    $"World cleanup completed ({trigger}): items={result.RemovedItems}, " +
                    $"projectiles={result.RemovedProjectiles}, skipped_items={result.SkippedItems}, " +
                    $"skipped_projectiles={result.SkippedProjectiles}.");
                break;
            case AutoWorldCleanerRunStatus.Cancelled:
                UpdateCompletedStatus(WorldCleanupRunState.Skipped, 0, 0, "cancelled");
                break;
            case AutoWorldCleanerRunStatus.Failed:
                UpdateCompletedStatus(WorldCleanupRunState.Failed, 0, 0, result.ErrorCode);
                logger.Log(
                    OperationsLogLevel.Warning,
                    "auto-world-cleaner",
                    $"World cleanup could not run ({trigger}): {result.ErrorCode ?? "unknown"}.");
                break;
        }
    }

    private void UpdateCompletedStatus(
        WorldCleanupRunState state,
        int removedItems,
        int removedProjectiles,
        string? detail)
    {
        WorldCleanupStatusSnapshot current = Read();
        Volatile.Write(
            ref status,
            current with
            {
                State = state,
                LastCompletedAt = timeProvider.GetUtcNow(),
                LastRemovedItems = removedItems,
                LastRemovedProjectiles = removedProjectiles,
                Detail = detail
            });
    }
}
