using Vega.Contracts.Worlds;
using Vega.Contracts.Players;

namespace Vega.Contracts.Sandboxes;

public enum SandboxPersistenceMode
{
    Temporary,
    Persistent,
    External
}

public enum SandboxAccessMode
{
    Public,
    Permission,
    AllowList
}

public enum SandboxSscMode
{
    Inherit,
    CopyOnEnter,
    Isolated,
    Disabled
}

public enum SandboxLifecycleState
{
    Defined,
    Materializing,
    Starting,
    Ready,
    Stopping,
    Stopped,
    Resetting,
    Destroying,
    Degraded,
    Failed
}

public sealed record SandboxAccessPolicy(
    SandboxAccessMode Mode,
    string? Permission = null,
    IReadOnlySet<string>? AllowedAccounts = null);

public sealed record SandboxDescriptor(
    WorldId WorldId,
    string DisplayName,
    string SourceWorldPath,
    string InstanceWorldPath,
    SandboxPersistenceMode Persistence,
    int PlayerCapacity,
    SandboxAccessPolicy Access,
    TimeSpan? IdleTimeToLive,
    SandboxSscMode SscMode,
    IReadOnlySet<string> EnabledModules,
    int ListenerPort,
    long Revision,
    DateTimeOffset CreatedAt,
    string TemplateRevision);

public sealed record SandboxCreateRequest(
    string WorldId,
    string DisplayName,
    string SourceWorldPath,
    SandboxPersistenceMode Persistence,
    int PlayerCapacity,
    SandboxAccessPolicy Access,
    TimeSpan? IdleTimeToLive,
    SandboxSscMode SscMode,
    IReadOnlySet<string> EnabledModules,
    int? ListenerPort = null);

public sealed record SandboxEndpoint(string Host, int Port);

public sealed record SandboxResourceSnapshot(
    double CpuPercent,
    long WorkingSetBytes,
    long StorageBytes);

public sealed record SandboxStatus(
    SandboxDescriptor Descriptor,
    SandboxLifecycleState State,
    SandboxEndpoint? Endpoint,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? ReadyAt,
    int OnlinePlayers,
    double TicksPerSecond,
    SandboxResourceSnapshot Resources,
    int RestartCount,
    string? Failure,
    bool DesiredRunning);

public enum SandboxOperationStatus
{
    Applied,
    AlreadyInDesiredState,
    NotFound,
    Conflict,
    Rejected,
    TimedOut,
    Failed
}

public sealed record SandboxOperationResult(
    SandboxOperationStatus Status,
    SandboxStatus? Sandbox = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsSuccess => Status is SandboxOperationStatus.Applied or SandboxOperationStatus.AlreadyInDesiredState;
}

public interface ISandboxControlService : IDisposable
{
    IReadOnlyList<SandboxStatus> Snapshot();

    ValueTask<SandboxOperationResult> CreateAsync(
        SandboxCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> StartAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> StopAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> RestartAsync(WorldId worldId, CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> ResetAsync(
        WorldId worldId,
        string? replacementSource = null,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxOperationResult> DestroyAsync(WorldId worldId, CancellationToken cancellationToken = default);
}

public sealed record SandboxTransferTicket(
    Guid TicketId,
    string AccountId,
    WorldId OriginWorldId,
    WorldId TargetWorldId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce,
    string Signature);

public enum SandboxTransferValidationStatus
{
    Accepted,
    InvalidSignature,
    Expired,
    Replayed,
    WrongTarget
}

public interface ISandboxTransferTicketService
{
    SandboxTransferTicket Issue(string accountId, WorldId originWorldId, WorldId targetWorldId);

    SandboxTransferValidationStatus ValidateAndConsume(
        SandboxTransferTicket ticket,
        WorldId receivingWorldId,
        DateTimeOffset now);
}

public sealed record SandboxTransferResult(
    bool Accepted,
    WorldId EffectiveWorldId,
    SandboxEndpoint? Endpoint,
    SandboxTransferTicket? Ticket,
    string? ErrorCode = null,
    string? Message = null);

public interface ISandboxRoutingService
{
    SandboxTransferResult PrepareEnter(
        string accountId,
        IReadOnlySet<string> permissions,
        WorldId originWorldId,
        WorldId targetWorldId);

    SandboxTransferResult PrepareLeave(string accountId, WorldId originWorldId);

    SandboxTransferResult PrepareTransfer(
        string accountId,
        IReadOnlySet<string> permissions,
        WorldId originWorldId,
        WorldId targetWorldId);

    SandboxTransferResult AcceptTransfer(SandboxTransferTicket ticket, WorldId receivingWorldId);
}

public sealed record SandboxPlayerTransferResult(bool Applied, string? ErrorCode = null);

public interface ISandboxPlayerTransferBoundary
{
    ValueTask<SandboxPlayerTransferResult> TransferAsync(
        PlayerId playerId,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket,
        CancellationToken cancellationToken = default);
}

public sealed record SandboxBackendHeartbeat(
    WorldId WorldId,
    Guid InstanceId,
    SandboxLifecycleState State,
    int ProcessId,
    DateTimeOffset Timestamp,
    DateTimeOffset StartedAt,
    int OnlinePlayers,
    double TicksPerSecond,
    long WorkingSetBytes,
    string RuntimeVersion,
    string Signature);
