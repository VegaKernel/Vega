using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Worlds;

public sealed record WorldAutoSaveOptions(bool Enabled, int IntervalMinutes)
{
    public static WorldAutoSaveOptions Default { get; } = new(true, 10);
}

public sealed record WorldItemCleanupOptions(
    bool Enabled,
    int MinimumActiveCount,
    int MinimumAgeSeconds)
{
    public static WorldItemCleanupOptions Default { get; } = new(true, 200, 300);
}

public sealed record WorldProjectileCleanupOptions(
    bool Enabled,
    int MinimumActiveCount,
    bool ExcludeMinions,
    bool ExcludeSentries)
{
    public static WorldProjectileCleanupOptions Default { get; } = new(true, 700, true, true);
}

public sealed record WorldCleanupOptions(
    bool Enabled,
    int IntervalMinutes,
    bool OnlyWhenPlayersOnline,
    WorldItemCleanupOptions Items,
    WorldProjectileCleanupOptions Projectiles)
{
    public static WorldCleanupOptions Default { get; } = new(
        false,
        30,
        true,
        WorldItemCleanupOptions.Default,
        WorldProjectileCleanupOptions.Default);
}

public sealed record WorldMaintenanceOptions(
    WorldAutoSaveOptions AutoSave,
    WorldCleanupOptions Cleanup)
{
    public static WorldMaintenanceOptions Default { get; } = new(
        WorldAutoSaveOptions.Default,
        WorldCleanupOptions.Default);

    public static WorldMaintenanceOptions Normalize(WorldMaintenanceOptions? candidate)
    {
        WorldAutoSaveOptions autoSave = candidate?.AutoSave ?? WorldAutoSaveOptions.Default;
        WorldCleanupOptions cleanup = candidate?.Cleanup ?? WorldCleanupOptions.Default;
        WorldItemCleanupOptions items = cleanup.Items ?? WorldItemCleanupOptions.Default;
        WorldProjectileCleanupOptions projectiles =
            cleanup.Projectiles ?? WorldProjectileCleanupOptions.Default;

        return new WorldMaintenanceOptions(
            new WorldAutoSaveOptions(
                autoSave.Enabled,
                Math.Clamp(autoSave.IntervalMinutes, 1, 1440)),
            new WorldCleanupOptions(
                cleanup.Enabled,
                Math.Clamp(cleanup.IntervalMinutes, 1, 1440),
                cleanup.OnlyWhenPlayersOnline,
                new WorldItemCleanupOptions(
                    items.Enabled,
                    Math.Clamp(items.MinimumActiveCount, 1, 1000),
                    Math.Clamp(items.MinimumAgeSeconds, 0, 86400)),
                new WorldProjectileCleanupOptions(
                    projectiles.Enabled,
                    Math.Clamp(projectiles.MinimumActiveCount, 1, 5000),
                    projectiles.ExcludeMinions,
                    projectiles.ExcludeSentries)));
    }
}

public static class WorldMaintenanceOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static WorldMaintenanceOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return File.Exists(path)
                ? WorldMaintenanceOptions.Normalize(
                    JsonSerializer.Deserialize<WorldMaintenanceOptions>(
                        File.ReadAllText(path),
                        SerializerOptions))
                : WorldMaintenanceOptions.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-maintenance",
                $"World-maintenance configuration '{Path.GetFullPath(path)}' is invalid; " +
                "stock-compatible autosave remains enabled and cleanup remains disabled.",
                ex);
            return WorldMaintenanceOptions.Default;
        }
    }
}
