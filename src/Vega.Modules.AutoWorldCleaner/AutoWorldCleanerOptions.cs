using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Modules.AutoWorldCleaner;

public sealed record AutoWorldCleanerItemOptions(
    bool Enabled,
    int MinimumActiveCount,
    int MinimumAgeSeconds)
{
    public static AutoWorldCleanerItemOptions Default { get; } = new(true, 200, 300);
}

public sealed record AutoWorldCleanerProjectileOptions(
    bool Enabled,
    int MinimumActiveCount,
    bool ExcludeMinions,
    bool ExcludeSentries)
{
    public static AutoWorldCleanerProjectileOptions Default { get; } = new(true, 700, true, true);
}

public sealed record AutoWorldCleanerOptions(
    bool Enabled,
    int IntervalMinutes,
    bool OnlyWhenPlayersOnline,
    AutoWorldCleanerItemOptions Items,
    AutoWorldCleanerProjectileOptions Projectiles)
{
    public static AutoWorldCleanerOptions Default { get; } = new(
        false,
        30,
        true,
        AutoWorldCleanerItemOptions.Default,
        AutoWorldCleanerProjectileOptions.Default);

    public static AutoWorldCleanerOptions Normalize(AutoWorldCleanerOptions? candidate)
    {
        AutoWorldCleanerItemOptions items = candidate?.Items ?? AutoWorldCleanerItemOptions.Default;
        AutoWorldCleanerProjectileOptions projectiles =
            candidate?.Projectiles ?? AutoWorldCleanerProjectileOptions.Default;

        return new AutoWorldCleanerOptions(
            candidate?.Enabled ?? Default.Enabled,
            Math.Clamp(candidate?.IntervalMinutes ?? Default.IntervalMinutes, 1, 1440),
            candidate?.OnlyWhenPlayersOnline ?? Default.OnlyWhenPlayersOnline,
            new AutoWorldCleanerItemOptions(
                items.Enabled,
                Math.Clamp(items.MinimumActiveCount, 1, 1000),
                Math.Clamp(items.MinimumAgeSeconds, 0, 86400)),
            new AutoWorldCleanerProjectileOptions(
                projectiles.Enabled,
                Math.Clamp(projectiles.MinimumActiveCount, 1, 5000),
                projectiles.ExcludeMinions,
                projectiles.ExcludeSentries));
    }
}

public static class AutoWorldCleanerOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static AutoWorldCleanerOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return File.Exists(path)
                ? AutoWorldCleanerOptions.Normalize(
                    JsonSerializer.Deserialize<AutoWorldCleanerOptions>(
                        File.ReadAllText(path),
                        SerializerOptions))
                : AutoWorldCleanerOptions.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "auto-world-cleaner",
                $"AutoWorldCleaner configuration '{Path.GetFullPath(path)}' is invalid; automatic cleanup remains disabled.",
                ex);
            return AutoWorldCleanerOptions.Default;
        }
    }
}
