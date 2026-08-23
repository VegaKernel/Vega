using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vega.Backups;

public sealed record BackupOptions(
    bool Enabled,
    string Interval,
    int RetainedBackups)
{
    public static BackupOptions Default { get; } = new(false, "06:00:00", 7);

    public TimeSpan ParseInterval()
    {
        if (!TimeSpan.TryParse(Interval, CultureInfo.InvariantCulture, out TimeSpan value) ||
            value < TimeSpan.FromMinutes(5) || value > TimeSpan.FromDays(30))
        {
            throw new InvalidDataException("backup interval must be between 00:05:00 and 30.00:00:00.");
        }

        return value;
    }
}

public static class BackupOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static BackupOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        BackupOptions options;
        try
        {
            options = File.Exists(path)
                ? JsonSerializer.Deserialize<BackupOptions>(File.ReadAllText(path), JsonOptions) ??
                  throw new InvalidDataException($"Backup configuration '{Path.GetFullPath(path)}' contains JSON null.")
                : BackupOptions.Default;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Backup configuration '{Path.GetFullPath(path)}' is invalid: {ex.Message}", ex);
        }

        _ = options.ParseInterval();
        if (options.RetainedBackups is < 1 or > 365)
        {
            throw new InvalidDataException("retainedBackups must be between 1 and 365.");
        }

        return options;
    }
}
