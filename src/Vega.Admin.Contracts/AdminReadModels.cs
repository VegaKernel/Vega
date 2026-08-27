namespace Vega.Admin.Contracts;

public sealed record AdminPlayerSnapshot(
    int Slot,
    string Name,
    string WorldId,
    bool IsDead,
    int Life,
    int MaxLife,
    int Mana,
    int MaxMana,
    bool IsHostile,
    int Team,
    string Platform,
    DateTimeOffset? ConnectedAt);

public sealed record AdminModuleSnapshot(
    string Name,
    string Version,
    string State,
    IReadOnlyList<string> Dependencies,
    long StartupMilliseconds,
    bool IsHotReloadable,
    string? Failure = null);

public enum AdminLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public sealed record AdminLogQuery(
    AdminLogLevel? MinimumLevel = null,
    string? Source = null,
    string? Text = null,
    int Limit = 100);

public sealed record AdminLogRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    AdminLogLevel Level,
    string Source,
    string Text);

public sealed record AdminChatRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Source,
    string Text);

public sealed record AdminPermissionGroupSnapshot(
    string Name,
    string? ParentName,
    IReadOnlyList<string> GrantedPermissions,
    IReadOnlyList<string> DeniedPermissions,
    int AccountCount,
    long Revision);

public sealed record AdminAccountSnapshot(
    string Name,
    string GroupName,
    DateTimeOffset? LastSeenUtc,
    string? Culture);

public sealed record AdminAdministrationSnapshot(
    IReadOnlyList<AdminPermissionGroupSnapshot> Groups,
    IReadOnlyList<AdminAccountSnapshot> Accounts,
    DateTimeOffset SampledAt);

public sealed record AdminDiagnosticsSnapshot(
    double CurrentTicksPerSecond,
    double AverageTicksPerSecondOneMinute,
    double TickDurationP50Milliseconds,
    double TickDurationP95Milliseconds,
    double TickDurationP99Milliseconds,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    long GcHeapBytes,
    long GcFragmentedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int AvailableWorkerThreads,
    int MaximumWorkerThreads,
    long InboundPackets,
    long InboundBytes,
    long OutboundPackets,
    long OutboundBytes,
    long RejectedInboundPackets,
    long SecurityViolations,
    IReadOnlyDictionary<string, long> SecurityRuleCounts,
    DateTimeOffset SampledAt);
