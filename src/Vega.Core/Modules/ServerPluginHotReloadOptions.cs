using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Core.Modules;

public sealed record ServerPluginHotReloadOptions(
    bool Enabled,
    int DebounceMilliseconds,
    int UnloadTimeoutMilliseconds)
{
    public int FaultThreshold { get; init; } = 5;
    public int FaultWindowSeconds { get; init; } = 60;
    /// <summary>Plugin name to explicit group names it may assign or replace. Empty means read-only identity.</summary>
    public Dictionary<string, string[]> IdentityGroupAssignments { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static ServerPluginHotReloadOptions Default { get; } = new(false, 750, 5000);

    public static ServerPluginHotReloadOptions Normalize(ServerPluginHotReloadOptions? candidate) => new(
        candidate?.Enabled ?? Default.Enabled,
        Math.Clamp(candidate?.DebounceMilliseconds ?? Default.DebounceMilliseconds, 100, 10000),
        Math.Clamp(candidate?.UnloadTimeoutMilliseconds ?? Default.UnloadTimeoutMilliseconds, 500, 30000))
    {
        FaultThreshold = Math.Clamp(candidate?.FaultThreshold ?? Default.FaultThreshold, 1, 100),
        FaultWindowSeconds = Math.Clamp(candidate?.FaultWindowSeconds ?? Default.FaultWindowSeconds, 1, 3600),
        IdentityGroupAssignments = (candidate?.IdentityGroupAssignments ?? new()).Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null)
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key,
                group => group.SelectMany(entry => entry.Value).Where(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && !name.Any(char.IsControl) && name != "*")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase)
    };
}

public static class ServerPluginHotReloadOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static ServerPluginHotReloadOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return File.Exists(path)
                ? ServerPluginHotReloadOptions.Normalize(
                    JsonSerializer.Deserialize<ServerPluginHotReloadOptions>(File.ReadAllText(path), SerializerOptions))
                : ServerPluginHotReloadOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "plugin-loader",
                $"Plugin hot-reload configuration '{Path.GetFullPath(path)}' is invalid; hot reload remains disabled.",
                ex);
            return ServerPluginHotReloadOptions.Default;
        }
    }
}
