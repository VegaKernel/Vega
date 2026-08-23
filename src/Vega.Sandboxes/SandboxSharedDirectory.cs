using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

/// <summary>
/// Read-only cross-process view used by sandbox backends. It reads the same persistent catalog as
/// the supervisor and accepts runtime state only from signed backend heartbeats.
/// </summary>
public sealed class SandboxSharedDirectory : ISandboxDirectory
{
    private static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(8);
    private readonly string dataRoot;
    private readonly byte[] authenticationKey;

    public SandboxSharedDirectory(string baseDirectory)
    {
        dataRoot = Path.Combine(Path.GetFullPath(baseDirectory), "data", "sandboxes");
        authenticationKey = SandboxKeyFile.LoadOrCreate(Path.Combine(dataRoot, "control.key"));
    }

    public IReadOnlyList<SandboxStatus> Snapshot()
    {
        var catalog = new SandboxCatalog(Path.Combine(dataRoot, "catalog.json"));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return catalog.Snapshot()
            .Select(descriptor => Snapshot(descriptor, now))
            .OrderBy(static status => status.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private SandboxStatus Snapshot(SandboxDescriptor descriptor, DateTimeOffset now)
    {
        string statusPath = Path.Combine(
            dataRoot,
            "instances",
            descriptor.WorldId.Value,
            "backend-status.json");
        if (File.Exists(statusPath) &&
            SandboxHeartbeatCodec.TryRead(statusPath, authenticationKey, out SandboxBackendHeartbeat? heartbeat) &&
            heartbeat is not null &&
            heartbeat.WorldId == descriptor.WorldId &&
            now - heartbeat.Timestamp <= HeartbeatFreshness)
        {
            SandboxLifecycleState state = heartbeat.State;
            SandboxEndpoint? endpoint = state == SandboxLifecycleState.Ready
                ? new SandboxEndpoint("127.0.0.1", descriptor.ListenerPort)
                : null;
            return new SandboxStatus(
                descriptor,
                state,
                endpoint,
                heartbeat.ProcessId,
                heartbeat.StartedAt,
                heartbeat.Timestamp,
                state == SandboxLifecycleState.Ready ? heartbeat.Timestamp : null,
                heartbeat.OnlinePlayers,
                heartbeat.TicksPerSecond,
                new SandboxResourceSnapshot(0, heartbeat.WorkingSetBytes, StorageBytes(descriptor)),
                0,
                null,
                state is SandboxLifecycleState.Starting or SandboxLifecycleState.Ready);
        }

        return new SandboxStatus(
            descriptor,
            SandboxLifecycleState.Stopped,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            new SandboxResourceSnapshot(0, 0, StorageBytes(descriptor)),
            0,
            null,
            false);
    }

    private static long StorageBytes(SandboxDescriptor descriptor)
    {
        try
        {
            return File.Exists(descriptor.InstanceWorldPath)
                ? new FileInfo(descriptor.InstanceWorldPath).Length
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
