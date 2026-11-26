using System.Diagnostics;
using HookMain = HookEvents.Terraria.Main;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Replaces Terraria's fixed ten-minute dedicated-server autosave timer.
/// Entity cleanup policy belongs to Vega.Modules.AutoWorldCleaner.
/// </summary>
public sealed class TerrariaWorldMaintenanceService : IDisposable
{
    private readonly object saveSync = new();
    private readonly WorldMaintenanceOptions options;
    private readonly IOperationsLogger logger;
    private readonly IGameThreadDispatcher gameThread;
    private Task? saveTask;
    private long nextAutoSave;
    private bool attached;
    private bool disposed;

    public TerrariaWorldMaintenanceService(
        WorldMaintenanceOptions options,
        IOperationsLogger logger,
        IGameThreadDispatcher gameThread)
    {
        this.options = WorldMaintenanceOptions.Normalize(options);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.gameThread = gameThread ?? throw new ArgumentNullException(nameof(gameThread));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        nextAutoSave = Add(now, TimeSpan.FromMinutes(options.AutoSave.IntervalMinutes));
        HookMain.DoUpdate_AutoSave += OnUpdate;
        attached = true;
    }

    public void Dispose()
    {
        if (attached)
        {
            HookMain.DoUpdate_AutoSave -= OnUpdate;
            attached = false;
        }

        Task? pendingSave;
        lock (saveSync)
        {
            disposed = true;
            pendingSave = saveTask;
        }

        try
        {
            pendingSave?.GetAwaiter().GetResult();
        }
        catch
        {
            // SaveWorld already records the failure; disposal must continue releasing hooks.
        }
    }

    public async ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task save;
        if (gameThread.IsGameThread)
        {
            save = QueueSave("Manual");
        }
        else
        {
            var scheduled = new TaskCompletionSource<Task>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!gameThread.TryPost("manual-world-save", () =>
            {
                if (cancellationToken.IsCancellationRequested) scheduled.TrySetCanceled(cancellationToken);
                else scheduled.TrySetResult(QueueSave("Manual"));
            }))
            {
                throw new InvalidOperationException("The Terraria game-thread queue is full.");
            }

            save = await scheduled.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await save.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnUpdate(object? sender, HookMain.DoUpdate_AutoSaveEventArgs args)
    {
        // Vega owns the one autosave schedule. Calling Terraria's original method would
        // retain its hard-coded ten-minute timer as a second schedule.
        args.ContinueExecution = false;
        if (global::Terraria.Main.gameMenu || global::Terraria.Main.netMode != 2)
        {
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (options.AutoSave.Enabled && now >= nextAutoSave)
        {
            nextAutoSave = Add(now, TimeSpan.FromMinutes(options.AutoSave.IntervalMinutes));
            _ = QueueSave("Automatic");
        }
    }

    private Task QueueSave(string origin)
    {
        lock (saveSync)
        {
            if (disposed)
            {
                return Task.FromException(new ObjectDisposedException(GetType().Name));
            }

            if (saveTask is { IsCompleted: false })
            {
                return saveTask;
            }

            try
            {
                // Snapshot mutable world state on Terraria's game thread before the
                // filesystem work moves to a worker, matching Terraria's saveAndPlay path.
                global::Terraria.IO.WorldFile.SetTempToOngoing();
            }
            catch (Exception ex)
            {
                logger.Log(
                    OperationsLogLevel.Error,
                    "world-maintenance",
                    $"{origin} world snapshot failed; save was not queued.",
                    ex);
                return Task.FromException(ex);
            }

            saveTask = Task.Run(() => SaveWorld(origin));
            _ = saveTask.ContinueWith(
                static failed => _ = failed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return saveTask;
        }
    }

    private void SaveWorld(string origin)
    {
        try
        {
            global::Terraria.IO.WorldFile.SaveWorld(
                resetTime: false,
                useTemps: true,
                canBeSkipped: false);
            logger.Log(
                OperationsLogLevel.Information,
                "world-maintenance",
                $"{origin} world save completed.");
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "world-maintenance",
                $"{origin} world save failed.",
                ex);
            throw;
        }
    }

    private static long Add(long timestamp, TimeSpan interval) =>
        checked(timestamp + (long)(interval.TotalSeconds * Stopwatch.Frequency));
}
