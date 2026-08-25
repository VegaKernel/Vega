using System.Text.Json;

namespace Vega.PluginSdk;

[Flags]
public enum VegaDebugCapabilities
{
    None = 0,
    Runtime = 1 << 0,
    Assemblies = 1 << 1,
    Threads = 1 << 2,
    Roots = 1 << 3,
    NonPublicMembers = 1 << 4,
    MethodInvoke = 1 << 5,
    StateMutation = 1 << 6,
    GarbageCollection = 1 << 7,
    NetworkTrace = 1 << 8
}

public sealed record VegaDebugRuntimeSnapshot(
    string FrameworkDescription,
    string RuntimeIdentifier,
    string ProcessArchitecture,
    int ProcessId,
    TimeSpan Uptime,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int ThreadPoolMaxWorkerThreads,
    int ThreadPoolMaxIoThreads,
    int ThreadPoolAvailableWorkerThreads,
    int ThreadPoolAvailableIoThreads,
    DateTimeOffset CapturedAt);

public sealed record VegaDebugAssemblySnapshot(
    string Name,
    string Version,
    string LoadContext,
    bool IsCollectible,
    bool IsDynamic,
    string? Location);

public sealed record VegaDebugThreadSnapshot(
    int Id,
    string? State,
    string? WaitReason,
    TimeSpan? TotalProcessorTime);

public sealed record VegaDebugRootSnapshot(
    string Name,
    string TypeName,
    string AssemblyName);

public enum VegaDebugMemberKind
{
    Field,
    Property
}

public sealed record VegaDebugMemberSnapshot(
    string Name,
    string TypeName,
    VegaDebugMemberKind Kind,
    bool IsPublic,
    bool IsStatic,
    bool CanRead,
    bool CanWrite,
    string? Value,
    string? Error);

public sealed record VegaDebugObjectSnapshot(
    string Root,
    string Path,
    string TypeName,
    IReadOnlyList<VegaDebugMemberSnapshot> Members);

public sealed record VegaDebugInvocationRequest(
    string Root,
    string Path,
    string Method,
    IReadOnlyList<JsonElement> Arguments);

public sealed record VegaDebugInvocationResult(
    bool Succeeded,
    string? ReturnType,
    string? Value,
    string? Error);

public sealed record VegaDebugMutationRequest(
    string Root,
    string Path,
    JsonElement Value);

public sealed record VegaDebugMutationResult(
    bool Succeeded,
    string? Error);

public sealed record VegaDebugNetworkTraceOptions(
    int DurationSeconds = 10,
    int MaxEvents = 4096,
    int? Slot = null,
    IReadOnlyList<int>? PacketIds = null,
    bool IncludeInbound = true,
    bool IncludeOutbound = true);

public sealed record VegaDebugNetworkPacket(
    DateTimeOffset Timestamp,
    string Direction,
    int? PacketId,
    int Size,
    int? Slot);

public sealed record VegaDebugPacketProcessorMetric(
    string Processor,
    string PacketType,
    long Calls,
    double TotalMicroseconds,
    double AverageMicroseconds,
    double MaxMicroseconds,
    long NonAllowDecisions,
    string? LastDecision,
    string? LastReason);

public sealed record VegaDebugNetworkTraceSnapshot(
    bool Active,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<VegaDebugNetworkPacket> Packets,
    IReadOnlyList<VegaDebugPacketProcessorMetric> ProcessorMetrics,
    long DroppedPackets);

/// <summary>
/// Process-internal diagnostics intended for explicitly trusted tooling plugins.
/// Dangerous capabilities are separately gated by the host configuration.
/// </summary>
public interface IVegaDebugContext
{
    VegaDebugCapabilities Capabilities { get; }

    VegaDebugRuntimeSnapshot CaptureRuntime();

    IReadOnlyList<VegaDebugAssemblySnapshot> SnapshotAssemblies();

    IReadOnlyList<VegaDebugThreadSnapshot> SnapshotThreads();

    IReadOnlyList<VegaDebugRootSnapshot> SnapshotRoots();

    VegaDebugObjectSnapshot Inspect(string root, string? path = null, int maxMembers = 128);

    ValueTask<VegaDebugInvocationResult> InvokeAsync(
        VegaDebugInvocationRequest request,
        CancellationToken cancellationToken = default);

    VegaDebugMutationResult SetValue(VegaDebugMutationRequest request);

    void CollectGarbage(int generation = -1, bool compact = false);

    /// <summary>
    /// Starts a bounded, temporary network and packet-pipeline trace. The trace automatically stops
    /// after the requested duration so diagnostics cannot accidentally become a permanent packet sniffer.
    /// </summary>
    void StartNetworkTrace(VegaDebugNetworkTraceOptions options)
    {
        EnsureDebugAccess();
        VegaDebugNetworkDiagnostics.Start(options);
    }

    void StopNetworkTrace()
    {
        EnsureDebugAccess();
        VegaDebugNetworkDiagnostics.Stop();
    }

    VegaDebugNetworkTraceSnapshot CaptureNetworkTrace()
    {
        EnsureDebugAccess();
        return VegaDebugNetworkDiagnostics.Capture();
    }

    private void EnsureDebugAccess()
    {
        if (Capabilities == VegaDebugCapabilities.None)
        {
            throw new InvalidOperationException("The Vega DEBUG API is disabled or this plugin is not trusted.");
        }
    }
}

public static class VegaDebug
{
    public static IVegaDebugContext Disabled { get; } = new DisabledDebugContext();

    private sealed class DisabledDebugContext : IVegaDebugContext
    {
        public VegaDebugCapabilities Capabilities => VegaDebugCapabilities.None;

        public VegaDebugRuntimeSnapshot CaptureRuntime() => throw Disabled();

        public IReadOnlyList<VegaDebugAssemblySnapshot> SnapshotAssemblies() => throw Disabled();

        public IReadOnlyList<VegaDebugThreadSnapshot> SnapshotThreads() => throw Disabled();

        public IReadOnlyList<VegaDebugRootSnapshot> SnapshotRoots() => throw Disabled();

        public VegaDebugObjectSnapshot Inspect(string root, string? path = null, int maxMembers = 128) =>
            throw Disabled();

        public ValueTask<VegaDebugInvocationResult> InvokeAsync(
            VegaDebugInvocationRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<VegaDebugInvocationResult>(Disabled());

        public VegaDebugMutationResult SetValue(VegaDebugMutationRequest request) => throw Disabled();

        public void CollectGarbage(int generation = -1, bool compact = false) => throw Disabled();

        public void StartNetworkTrace(VegaDebugNetworkTraceOptions options) => throw Disabled();

        public void StopNetworkTrace() => throw Disabled();

        public VegaDebugNetworkTraceSnapshot CaptureNetworkTrace() => throw Disabled();

        private static InvalidOperationException Disabled() =>
            new("The Vega DEBUG API is disabled or this plugin is not trusted.");
    }
}
