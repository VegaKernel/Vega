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

    public static ServerPluginHotReloadOptions Default { get; } = new(false, 750, 5000);

    public static ServerPluginHotReloadOptions Normalize(ServerPluginHotReloadOptions? candidate) => new(
        candidate?.Enabled ?? Default.Enabled,
        Math.Clamp(candidate?.DebounceMilliseconds ?? Default.DebounceMilliseconds, 100, 10000),
        Math.Clamp(candidate?.UnloadTimeoutMilliseconds ?? Default.UnloadTimeoutMilliseconds, 500, 30000))
    {
        FaultThreshold = Math.Clamp(candidate?.FaultThreshold ?? Default.FaultThreshold, 1, 100),
        FaultWindowSeconds = Math.Clamp(candidate?.FaultWindowSeconds ?? Default.FaultWindowSeconds, 1, 3600)
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
