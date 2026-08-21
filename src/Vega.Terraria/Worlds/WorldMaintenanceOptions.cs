using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Worlds;

public sealed record WorldAutoSaveOptions(bool Enabled, int IntervalMinutes)
{
    public static WorldAutoSaveOptions Default { get; } = new(true, 10);
}

public sealed record WorldMaintenanceOptions(WorldAutoSaveOptions AutoSave)
{
    public static WorldMaintenanceOptions Default { get; } = new(WorldAutoSaveOptions.Default);

    public static WorldMaintenanceOptions Normalize(WorldMaintenanceOptions? candidate)
    {
        WorldAutoSaveOptions autoSave = candidate?.AutoSave ?? WorldAutoSaveOptions.Default;
        return new WorldMaintenanceOptions(
            new WorldAutoSaveOptions(
                autoSave.Enabled,
                Math.Clamp(autoSave.IntervalMinutes, 1, 1440)));
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
                $"World-maintenance configuration '{Path.GetFullPath(path)}' is invalid; stock-compatible autosave remains enabled.",
                ex);
            return WorldMaintenanceOptions.Default;
        }
    }
}
