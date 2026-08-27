using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Runtime.Administration;

internal static class AdminConfigurationExposureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ManagedConfigurationAdministration Load(
        string path,
        IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        if (!File.Exists(path))
        {
            return new ManagedConfigurationAdministration();
        }

        try
        {
            string json = File.ReadAllText(path);
            AdminConfigurationExposureFile? file = JsonSerializer.Deserialize<AdminConfigurationExposureFile>(
                json,
                JsonOptions);
            return new ManagedConfigurationAdministration(file?.Documents ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "admin-configuration",
                "Managed configuration administration policy is invalid; exposure is disabled for this process.",
                ex);
            return new ManagedConfigurationAdministration();
        }
    }

    private sealed record AdminConfigurationExposureFile(
        IReadOnlyList<ManagedConfigurationExposureRule>? Documents = null);
}
