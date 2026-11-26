using System.Text.Json;

namespace Vega.Sandboxes;

public sealed record SandboxOptions(
    bool Enabled,
    int BasePort,
    int MaximumActiveWorlds,
    int MaximumConcurrentStarts,
    int StartTimeoutSeconds,
    int HeartbeatTimeoutSeconds,
    int GracefulStopTimeoutSeconds,
    int MaximumAutomaticRestarts,
    int RestartBackoffSeconds,
    int MaximumCpuPercent,
    int MaximumWorkingSetMegabytes,
    int MaximumInstanceMegabytes,
    int DefaultIdleTtlMinutes)
{
    /// <summary>Sandbox listeners are local by default; a separately deployed proxy may opt into another bind address.</summary>
    public string BackendBindAddress { get; init; } = "127.0.0.1";

    public static SandboxOptions Default { get; } = new(
        true, 7780, 8, 1, 60, 15, 15, 3, 5, 100, 2048, 4096, 30);
}

public static class SandboxOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static SandboxOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WriteAtomic(path, JsonSerializer.Serialize(SandboxOptions.Default, JsonOptions));
            return SandboxOptions.Default;
        }

        SandboxOptions options;
        try
        {
            options = JsonSerializer.Deserialize<SandboxOptions>(File.ReadAllText(path), JsonOptions) ??
                throw new InvalidDataException($"Sandbox configuration '{Path.GetFullPath(path)}' contains JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sandbox configuration '{Path.GetFullPath(path)}' is invalid: {ex.Message}", ex);
        }

        Validate(options);
        return options;
    }

    private static void Validate(SandboxOptions options)
    {
        if (!System.Net.IPAddress.TryParse(options.BackendBindAddress, out _) ||
            options.BasePort is < 1 or > 65535 ||
            options.MaximumActiveWorlds is < 1 or > 64 ||
            options.MaximumConcurrentStarts != 1 ||
            options.StartTimeoutSeconds is < 5 or > 600 ||
            options.HeartbeatTimeoutSeconds is < 5 or > 120 ||
            options.GracefulStopTimeoutSeconds is < 1 or > 120 ||
            options.MaximumAutomaticRestarts is < 0 or > 20 ||
            options.RestartBackoffSeconds is < 1 or > 300 ||
            options.MaximumCpuPercent is < 10 or > 100 ||
            options.MaximumWorkingSetMegabytes is < 256 or > 131_072 ||
            options.MaximumInstanceMegabytes is < 64 or > 1_048_576 ||
            options.DefaultIdleTtlMinutes is < 1 or > 10_080)
        {
            throw new InvalidDataException("Sandbox configuration contains a value outside its supported safety bounds.");
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
