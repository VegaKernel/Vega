using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed record SandboxBackendLaunchOptions(
    bool IsBackend,
    WorldId WorldId,
    Guid InstanceId,
    string StatusPath,
    byte[] AuthenticationKey,
    IReadOnlySet<string> EnabledModules,
    IReadOnlyList<string> ServerArguments)
{
    public static SandboxBackendLaunchOptions Parse(
        IEnumerable<string> arguments,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var remaining = new List<string>();
        string? worldId = null;
        string? instance = null;
        string? status = null;
        string? modules = null;
        foreach (string argument in arguments)
        {
            if (TryRead(argument, "--vega-backend-world-id", out string? value)) worldId = value;
            else if (TryRead(argument, "--vega-backend-instance", out value)) instance = value;
            else if (TryRead(argument, "--vega-backend-status", out value)) status = value;
            else if (TryRead(argument, "--vega-backend-modules", out value)) modules = value;
            else remaining.Add(argument);
        }

        bool requested = worldId is not null || instance is not null || status is not null;
        if (!requested)
        {
            return new SandboxBackendLaunchOptions(
                false, default, default, string.Empty, [], new HashSet<string>(), remaining);
        }

        if (string.IsNullOrWhiteSpace(worldId) || !Guid.TryParse(instance, out Guid instanceId) ||
            string.IsNullOrWhiteSpace(status) ||
            string.IsNullOrWhiteSpace(environment("VEGA_BACKEND_TOKEN")))
        {
            throw new ArgumentException("Incomplete authenticated Vega sandbox backend launch context.");
        }

        var id = new WorldId(worldId);
        SandboxMaterializer.ValidateWorldId(id);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(environment("VEGA_BACKEND_TOKEN")!);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("VEGA_BACKEND_TOKEN is invalid.", ex);
        }

        if (key.Length < 32)
        {
            throw new ArgumentException("VEGA_BACKEND_TOKEN must contain at least 256 bits.");
        }

        return new SandboxBackendLaunchOptions(
            true,
            id,
            instanceId,
            Path.GetFullPath(status),
            key,
            new HashSet<string>(
                (modules ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase),
            Array.AsReadOnly(remaining.ToArray()));
    }

    private static bool TryRead(string argument, string option, out string? value)
    {
        if (argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = argument[(option.Length + 1)..];
            return true;
        }

        value = null;
        return false;
    }
}

public sealed class SandboxBackendReporter : IDisposable
{
    private readonly SandboxBackendLaunchOptions launch;
    private readonly IRuntimeLifecycle lifecycle;
    private readonly IServerRuntimeStateReader server;
    private readonly IPlayerDirectory players;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public SandboxBackendReporter(
        SandboxBackendLaunchOptions launch,
        IRuntimeLifecycle lifecycle,
        IServerRuntimeStateReader server,
        IPlayerDirectory players)
    {
        if (!launch.IsBackend)
        {
            throw new ArgumentException("A backend launch context is required.", nameof(launch));
        }

        this.launch = launch;
        this.lifecycle = lifecycle;
        this.server = server;
        this.players = players;
        Write(CurrentState());
        worker = RunAsync(stopping.Token);
    }

    public void Dispose()
    {
        stopping.Cancel();
        try
        {
            worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        Write(SandboxLifecycleState.Stopped);
        stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            Write(CurrentState());
        }
    }

    private SandboxLifecycleState CurrentState() => lifecycle.CurrentStage >= RuntimeLifecycleStage.NetworkReady
        ? SandboxLifecycleState.Ready
        : SandboxLifecycleState.Starting;

    private void Write(SandboxLifecycleState state)
    {
        ServerRuntimeStateSnapshot runtime = server.Read();
        using Process process = Process.GetCurrentProcess();
        var unsigned = new SandboxBackendHeartbeat(
            launch.WorldId,
            launch.InstanceId,
            state,
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            startedAt,
            players.SnapshotOnlinePlayers().Count,
            runtime.TicksPerSecond,
            process.WorkingSet64,
            typeof(SandboxBackendReporter).Assembly.GetName().Version?.ToString() ?? "unknown",
            string.Empty);
        SandboxBackendHeartbeat heartbeat = unsigned with
        {
            Signature = SandboxHeartbeatCodec.Sign(unsigned, launch.AuthenticationKey)
        };
        string path = launch.StatusPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(heartbeat));
        File.Move(temporary, path, true);
    }
}

internal static class SandboxHeartbeatCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryRead(string path, byte[] key, out SandboxBackendHeartbeat? heartbeat)
    {
        heartbeat = null;
        try
        {
            SandboxBackendHeartbeat? parsed = JsonSerializer.Deserialize<SandboxBackendHeartbeat>(
                File.ReadAllText(path), JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            string expected = Sign(parsed with { Signature = string.Empty }, key);
            byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
            byte[] suppliedBytes = Encoding.UTF8.GetBytes(parsed.Signature);
            if (expectedBytes.Length != suppliedBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            {
                return false;
            }

            heartbeat = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Sign(SandboxBackendHeartbeat heartbeat, byte[] key)
    {
        string canonical = string.Join('|',
            heartbeat.WorldId.Value,
            heartbeat.InstanceId.ToString("N"),
            (int)heartbeat.State,
            heartbeat.ProcessId,
            heartbeat.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture),
            heartbeat.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            heartbeat.OnlinePlayers,
            heartbeat.TicksPerSecond.ToString("R", CultureInfo.InvariantCulture),
            heartbeat.WorkingSetBytes,
            heartbeat.RuntimeVersion);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
