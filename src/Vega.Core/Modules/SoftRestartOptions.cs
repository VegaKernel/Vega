using System.Text.Json;

namespace Vega.Core.Modules;

public sealed record SoftRestartOptions(
    bool Enabled,
    int DrainTimeoutSeconds,
    int ModuleTimeoutSeconds,
    int TotalTimeoutSeconds)
{
    public static SoftRestartOptions Default { get; } = new(false, 5, 10, 60);
}

public static class SoftRestartOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SoftRestartOptions Load(string path)
    {
        SoftRestartOptions value = File.Exists(path)
            ? JsonSerializer.Deserialize<SoftRestartOptions>(File.ReadAllText(path), JsonOptions) ??
              throw new InvalidDataException("Soft-restart configuration contains JSON null.")
            : SoftRestartOptions.Default;
        if (value.DrainTimeoutSeconds is < 1 or > 60 || value.ModuleTimeoutSeconds is < 1 or > 120 ||
            value.TotalTimeoutSeconds < value.ModuleTimeoutSeconds || value.TotalTimeoutSeconds > 600)
        {
            throw new InvalidDataException("Soft-restart timeouts are outside supported safety bounds.");
        }

        return value;
    }
}
