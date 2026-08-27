using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Runtime.Administration;

internal static class AdminConfigurationExposureStore
{
    public static ManagedConfigurationAdministration Load(
        string path,
        IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        ManagedConfigurationAdministration administration =
            ManagedConfigurationAdministration.LoadPolicy(path, out string? error);
        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "admin-configuration",
                $"Managed configuration administration policy '{path}' is invalid; exposure is disabled for this process. {error}");
        }

        return administration;
    }
}
