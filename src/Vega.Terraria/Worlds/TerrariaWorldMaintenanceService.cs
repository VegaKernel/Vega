using System.Diagnostics;
using HookMain = HookEvents.Terraria.Main;
using Vega.Contracts.Operations;

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
    private Task? saveTask;
    private long nextAutoSave;
    private bool attached;
    private bool disposed;

    public TerrariaWorldMaintenanceService(
        WorldMaintenanceOptions options,
        IOperationsLogger logger)
    {
        this.options = WorldMaintenanceOptions.Normalize(options);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        pendingSave?.GetAwaiter().GetResult();
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
            QueueAutoSave();
        }
    }

    private void QueueAutoSave()
    {
        lock (saveSync)
        {
            if (disposed || saveTask is { IsCompleted: false })
            {
                return;
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
                    "Automatic world snapshot failed; autosave was not queued.",
                    ex);
                return;
            }

            saveTask = Task.Run(SaveWorld);
        }
    }

    private void SaveWorld()
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
                "Automatic world save completed.");
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "world-maintenance",
                "Automatic world save failed.",
                ex);
        }
    }

    private static long Add(long timestamp, TimeSpan interval) =>
        checked(timestamp + (long)(interval.TotalSeconds * Stopwatch.Frequency));
}
