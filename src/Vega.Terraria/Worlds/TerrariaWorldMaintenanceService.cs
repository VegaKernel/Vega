using System.Diagnostics;
using HookMain = HookEvents.Terraria.Main;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Replaces Terraria's fixed ten-minute dedicated-server autosave timer and performs
/// optional bounded entity cleanup on the game thread.
/// </summary>
public sealed class TerrariaWorldMaintenanceService : IDisposable
{
    private readonly object saveSync = new();
    private readonly WorldMaintenanceOptions options;
    private readonly IOperationsLogger logger;
    private Task? saveTask;
    private long nextAutoSave;
    private long nextCleanup;
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
        nextCleanup = Add(now, TimeSpan.FromMinutes(options.Cleanup.IntervalMinutes));
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
        // The Vega configuration is the single source of truth. Calling Terraria's
        // original method would retain its hard-coded ten-minute timer as a second schedule.
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

        if (options.Cleanup.Enabled && now >= nextCleanup)
        {
            nextCleanup = Add(now, TimeSpan.FromMinutes(options.Cleanup.IntervalMinutes));
            if (!options.Cleanup.OnlyWhenPlayersOnline ||
                global::Terraria.Main.player.Any(static player => player?.active is true))
            {
                RunCleanup();
            }
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

    private void RunCleanup()
    {
        try
        {
            int removedItems = CleanItems(options.Cleanup.Items);
            int removedProjectiles = CleanProjectiles(options.Cleanup.Projectiles);
            logger.Log(
                OperationsLogLevel.Information,
                "world-maintenance",
                $"World cleanup completed: items={removedItems}, projectiles={removedProjectiles}.");
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "world-maintenance",
                "World cleanup failed.",
                ex);
        }
    }

    internal static int CleanItems(WorldItemCleanupOptions options)
    {
        if (!options.Enabled)
        {
            return 0;
        }

        int activeCount = global::Terraria.Main.item.Count(static item => item?.active is true);
        if (activeCount < options.MinimumActiveCount)
        {
            return 0;
        }

        int minimumAgeTicks = checked(options.MinimumAgeSeconds * 60);
        int removed = 0;
        for (int index = 0;
             index < global::Terraria.Main.maxItems && index < global::Terraria.Main.item.Length;
             index++)
        {
            global::Terraria.WorldItem? item = global::Terraria.Main.item[index];
            if (item is null || !item.active || item.timeSinceItemSpawned < minimumAgeTicks)
            {
                continue;
            }

            item.TurnToAir();
            global::Terraria.NetMessage.SendData(21, number: index);
            removed++;
        }

        return removed;
    }

    internal static int CleanProjectiles(WorldProjectileCleanupOptions options)
    {
        if (!options.Enabled)
        {
            return 0;
        }

        int activeCount = global::Terraria.Main.projectile.Count(projectile =>
            projectile?.active is true &&
            (!options.ExcludeMinions || !projectile.minion) &&
            (!options.ExcludeSentries || !projectile.sentry));
        if (activeCount < options.MinimumActiveCount)
        {
            return 0;
        }

        int removed = 0;
        for (int index = 0;
             index < global::Terraria.Main.maxProjectiles &&
             index < global::Terraria.Main.projectile.Length;
             index++)
        {
            global::Terraria.Projectile? projectile = global::Terraria.Main.projectile[index];
            if (projectile is null || !projectile.active ||
                (options.ExcludeMinions && projectile.minion) ||
                (options.ExcludeSentries && projectile.sentry))
            {
                continue;
            }

            int key = projectile.key;
            float positionX = projectile.position.X;
            float positionY = projectile.position.Y;
            projectile.active = false;
            projectile.timeLeft = 0;
            projectile.type = 0;

            global::Terraria.NetMessage.SendData(
                29,
                number: key,
                number2: positionX,
                number3: positionY);
            removed++;
        }

        return removed;
    }

    private static long Add(long timestamp, TimeSpan interval) =>
        checked(timestamp + (long)(interval.TotalSeconds * Stopwatch.Frequency));
}
