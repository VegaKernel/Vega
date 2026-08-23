using System.Diagnostics;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;

namespace Vega.Core.Modules;

public sealed record RestartableModuleGraph(
    IVegaModuleHost Host,
    IReadOnlyList<string> ModuleNames,
    Action Activate) : IDisposable
{
    public void Dispose() => Host.Dispose();
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
        active.Host.Start();
        active.Activate();
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
            previousGraph.Dispose();
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
        catch (Exception ex)
        {
            replacement?.Dispose();
            if (!activeDisposed)
            {
                lock (sync) accepting = true;
                logger.Log(OperationsLogLevel.Warning, "soft-restart",
                    "Soft restart stopped before the active module graph changed.", ex);
                return Finish(SoftRestartStatus.RolledBack, previous, previous, startedAt,
                    $"Restart stopped before the active generation changed: {ex.Message}",
                    modules ?? new HashSet<string>());
            }

            try
            {
                RestartableModuleGraph rollback = factory(null);
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
                lock (sync) accepting = false;
                logger.Log(OperationsLogLevel.Critical, "soft-restart", "Replacement and rollback failed; process restart required.",
                    new AggregateException(ex, rollbackError));
                return Finish(SoftRestartStatus.Degraded, previous, generation, startedAt,
                    "Replacement and rollback failed; process restart is required.", modules ?? new HashSet<string>());
            }
        }
        finally
        {
            restart.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (sync)
        {
            accepting = false;
            active.Dispose();
        }
        restart.Dispose();
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

    private Task StartAsync(RestartableModuleGraph graph, CancellationToken cancellationToken) =>
        graph.Host is VegaModuleHost host
            ? host.StartAsync(TimeSpan.FromSeconds(options.ModuleTimeoutSeconds), cancellationToken)
            : Task.Run(graph.Host.Start, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(options.ModuleTimeoutSeconds), cancellationToken);

    private sealed class CallbackLease(SoftRestartCoordinator owner, long generation) : IModuleCallbackLease
    {
        private SoftRestartCoordinator? owner = owner;
        public long Generation { get; } = generation;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseCallback();
    }
}
