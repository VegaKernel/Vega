using System.Diagnostics;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;

namespace Vega.Core.Modules;

public sealed record RestartableModuleGraph(
    IVegaModuleHost Host,
    IReadOnlyList<string> ModuleNames,
    Action Activate) : IDisposable
{
    private int retired;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref retired, 1) == 0) Host.Dispose();
    }
}

public sealed class SoftRestartCoordinator : ISoftRestartService, IModuleOperationsCatalog, IDisposable
{
    private readonly object sync = new();
    private readonly SoftRestartOptions options;
    private readonly Func<IReadOnlySet<string>?, RestartableModuleGraph> factory;
    private readonly IOperationsLogger logger;
    private readonly SemaphoreSlim restart = new(1, 1);
    private RestartableModuleGraph active;
    private TaskCompletionSource drained = CompletedDrain();
    private long generation = 1;
    private int inFlight;
    private bool accepting = true;
    private bool disposed;
    private Func<string?>? processRestartGuard;
    private SoftRestartSnapshot snapshot;

    public SoftRestartCoordinator(
        SoftRestartOptions options,
        Func<IReadOnlySet<string>?, RestartableModuleGraph> factory,
        IOperationsLogger logger)
    {
        this.options = options;
        this.factory = factory;
        this.logger = logger;
        active = factory(null);
        try
        {
            active.Host.Start();
            active.Activate();
        }
        catch (Exception startupError)
        {
            try { active.Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(startupError, cleanupError); }
            throw;
        }
        snapshot = new SoftRestartSnapshot(options.Enabled, false, generation, null, null, null, null);
    }

    public void SetProcessRestartGuard(Func<string?> guard) =>
        processRestartGuard = guard ?? throw new ArgumentNullException(nameof(guard));

    public SoftRestartSnapshot Read()
    {
        lock (sync) return snapshot;
    }

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules()
    {
        lock (sync) return active.Host.SnapshotModules();
    }

    public bool TryAcquireCallback(out IModuleCallbackLease? lease)
    {
        lock (sync)
        {
            if (!accepting || disposed)
            {
                lease = null;
                return false;
            }

            if (inFlight++ == 0) drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease = new CallbackLease(this, generation);
            return true;
        }
    }

    public async ValueTask<SoftRestartResult> RestartAsync(
        IReadOnlySet<string>? modules,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) return Result(SoftRestartStatus.Disabled, generation, generation, TimeSpan.Zero,
            "Soft restart is disabled in config/soft-restart.json.", modules ?? new HashSet<string>());
        string? restartReason = processRestartGuard?.Invoke();
        if (restartReason is not null)
            return Result(SoftRestartStatus.ProcessRestartRequired, generation, generation, TimeSpan.Zero,
                restartReason, modules ?? new HashSet<string>());
        if (!await restart.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result(SoftRestartStatus.Busy, generation, generation, TimeSpan.Zero,
                "Another soft restart is already running.", modules ?? new HashSet<string>());

        long startedAt = Stopwatch.GetTimestamp();
        long previous = generation;
        RestartableModuleGraph? replacement = null;
        bool activeDisposed = false;
        IReadOnlyDictionary<string, ModuleRestartState> handoff = new Dictionary<string, ModuleRestartState>();
        try
        {
            lock (sync)
            {
                if (disposed || snapshot.LastStatus == SoftRestartStatus.Degraded)
                    return Result(SoftRestartStatus.ProcessRestartRequired, previous, previous, TimeSpan.Zero,
                        "Module host requires a process restart.", modules ?? new HashSet<string>());
            }
            lock (sync) snapshot = snapshot with { IsRestarting = true, LastStartedAt = DateTimeOffset.UtcNow, Detail = "Validating replacement graph." };
            logger.Log(OperationsLogLevel.Warning, "soft-restart",
                $"Preparing replacement for module generation {previous}.");
            string[] missing;
            lock (sync)
            {
                missing = modules?.Where(name => !Matches(active.ModuleNames, name)).ToArray() ?? [];
            }
            if (missing.Length > 0)
            {
                return Finish(SoftRestartStatus.ModuleNotFound, previous, previous, startedAt,
                    $"Unknown restartable module(s): {string.Join(", ", missing)}.", modules ?? new HashSet<string>());
            }

            replacement = factory(modules);

            Task drain;
            lock (sync)
            {
                accepting = false;
                drain = inFlight == 0 ? Task.CompletedTask : drained.Task;
                snapshot = snapshot with { Detail = "Draining module callbacks." };
            }

            logger.Log(OperationsLogLevel.Information, "soft-restart", "Draining active module callbacks.");

            await drain.WaitAsync(TimeSpan.FromSeconds(options.DrainTimeoutSeconds), cancellationToken).ConfigureAwait(false);
            RestartableModuleGraph previousGraph;
            lock (sync) previousGraph = active;
            handoff = previousGraph.Host is VegaModuleHost previousHost
                ? previousHost.CaptureRestartState()
                : new Dictionary<string, ModuleRestartState>();
            activeDisposed = true;
            try { previousGraph.Dispose(); }
            catch (Exception cleanupError)
            {
                return Degraded("The active module graph failed to release its resources.", DisposeAfterFailure(replacement, cleanupError));
            }
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            total.CancelAfter(TimeSpan.FromSeconds(options.TotalTimeoutSeconds));
            if (replacement.Host is VegaModuleHost replacementHost)
            {
                replacementHost.RestoreRestartState(handoff);
            }
            await StartAsync(replacement, total.Token).ConfigureAwait(false);
            replacement.Activate();
            lock (sync)
            {
                active = replacement;
                replacement = null;
                generation++;
                accepting = true;
            }

            logger.Log(OperationsLogLevel.Warning, "soft-restart", $"Module generation {previous} replaced by {generation}.");
            return Finish(SoftRestartStatus.Succeeded, previous, generation, startedAt,
                "Restartable module graph replaced; Terraria, world, players, persistence and Operations were preserved.",
                modules ?? active.ModuleNames.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (ModuleStartupInProgressException ex)
        {
            logger.Log(OperationsLogLevel.Critical, "soft-restart", ex.Message, ex);
            return Finish(SoftRestartStatus.Degraded, previous, previous, startedAt, ex.Message, modules ?? new HashSet<string>());
        }
        catch (Exception ex)
        {
            Exception failure = DisposeAfterFailure(replacement, ex);
            if (!ReferenceEquals(failure, ex))
                return Degraded("The replacement module graph failed to release its resources.", failure);
            if (!activeDisposed)
            {
                lock (sync) accepting = true;
                logger.Log(OperationsLogLevel.Warning, "soft-restart",
                    "Soft restart stopped before the active module graph changed.", ex);
                return Finish(SoftRestartStatus.RolledBack, previous, previous, startedAt,
                    $"Restart stopped before the active generation changed: {ex.Message}",
                    modules ?? new HashSet<string>());
            }

            RestartableModuleGraph? rollback = null;
            try
            {
                rollback = factory(null);
                if (rollback.Host is VegaModuleHost rollbackHost)
                {
                    rollbackHost.RestoreRestartState(handoff);
                }
                await StartAsync(rollback, CancellationToken.None).ConfigureAwait(false);
                rollback.Activate();
                lock (sync)
                {
                    active = rollback;
                    generation++;
                    accepting = true;
                }
                logger.Log(OperationsLogLevel.Error, "soft-restart", "Replacement failed; a fresh previous graph was restored.", ex);
                return Finish(SoftRestartStatus.RolledBack, previous, generation, startedAt,
                    $"Replacement failed and was rolled back: {ex.Message}", modules ?? new HashSet<string>());
            }
            catch (Exception rollbackError)
            {
                Exception rollbackFailure = rollbackError is ModuleStartupInProgressException
                    ? rollbackError : DisposeAfterFailure(rollback, rollbackError);
                return Degraded("Replacement and rollback failed.", new AggregateException(ex, rollbackFailure));
            }
        }
        finally
        {
            restart.Release();
        }

        SoftRestartResult Degraded(string detail, Exception failure)
        {
            lock (sync) accepting = false;
            logger.Log(OperationsLogLevel.Critical, "soft-restart", detail, failure);
            return Finish(SoftRestartStatus.Degraded, previous, generation, startedAt,
                detail + " A process restart is required.", modules ?? new HashSet<string>());
        }
    }

    private static Exception DisposeAfterFailure(RestartableModuleGraph? graph, Exception failure)
    {
        try { graph?.Dispose(); }
        catch (Exception cleanupError) { return new AggregateException(failure, cleanupError); }
        return failure;
    }

    public void Dispose()
    {
        restart.Wait();
        try
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                accepting = false;
            }
            active.Dispose();
        }
        finally { restart.Release(); }
    }

    private void ReleaseCallback()
    {
        lock (sync)
        {
            if (--inFlight == 0) drained.TrySetResult();
        }
    }

    private SoftRestartResult Finish(SoftRestartStatus status, long previous, long current, long startedAt,
        string message, IEnumerable<string> modules)
    {
        TimeSpan duration = Stopwatch.GetElapsedTime(startedAt);
        lock (sync) snapshot = new SoftRestartSnapshot(options.Enabled, false, current, snapshot.LastStartedAt,
            DateTimeOffset.UtcNow, status, message);
        return Result(status, previous, current, duration, message, modules);
    }

    private static SoftRestartResult Result(SoftRestartStatus status, long previous, long current, TimeSpan duration,
        string message, IEnumerable<string> modules) => new(status, previous, current, duration, message,
        modules.ToArray());

    private static bool Matches(IEnumerable<string> moduleNames, string requested) => moduleNames.Any(name =>
        string.Equals(name, requested, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name[(name.LastIndexOf('.') + 1)..], requested, StringComparison.OrdinalIgnoreCase));

    private static TaskCompletionSource CompletedDrain()
    {
        var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        value.SetResult();
        return value;
    }

    private async Task StartAsync(RestartableModuleGraph graph, CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(options.ModuleTimeoutSeconds);
        if (graph.Host is VegaModuleHost host)
            await host.StartAsync(timeout, cancellationToken).ConfigureAwait(false);
        else
        {
            try { await ModuleStartupInProgressException.RunAsync(graph.Host.Start, timeout, cancellationToken).ConfigureAwait(false); }
            catch (ModuleStartupInProgressException ex) { ex.Retire(graph.Dispose); throw; }
        }
    }

    private sealed class CallbackLease(SoftRestartCoordinator owner, long generation) : IModuleCallbackLease
    {
        private SoftRestartCoordinator? owner = owner;
        public long Generation { get; } = generation;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseCallback();
    }
}
