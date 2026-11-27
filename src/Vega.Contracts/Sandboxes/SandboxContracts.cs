using Vega.Contracts.Worlds;
using Vega.Contracts.Players;
using Vega.Contracts.Accounts;

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
    int? ListenerPort = null)
{
    /// <summary>Build a new immutable template from SourceWorldPath and a supported portable schematic.</summary>
    public SandboxSchematicBootstrap? Schematic { get; init; }
}

public sealed record SandboxSchematicBootstrap(string SchematicPath, int X, int Y);

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

public interface ISandboxDirectory
{
    IReadOnlyList<SandboxStatus> Snapshot();
}

public interface ISandboxControlService : ISandboxDirectory, IDisposable
{

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

    /// <summary>Destructive external administration must revalidate the observed descriptor under the service's mutation gate.</summary>
    ValueTask<SandboxOperationResult> ResetConfirmedAsync(WorldId worldId, long expectedRevision, DateTimeOffset expectedCreatedAt,
        string? replacementSource = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SandboxOperationResult(SandboxOperationStatus.Rejected, ErrorCode: "sandbox.confirmed_mutation_unsupported"));

    ValueTask<SandboxOperationResult> DestroyConfirmedAsync(WorldId worldId, long expectedRevision, DateTimeOffset expectedCreatedAt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SandboxOperationResult(SandboxOperationStatus.Rejected, ErrorCode: "sandbox.confirmed_mutation_unsupported"));
}

public sealed record SandboxTransferTicket(
    Guid TicketId,
    string AccountId,
    WorldId OriginWorldId,
    WorldId TargetWorldId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Nonce,
    string Signature,
    Guid? SscLeaseId = null);

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
    SandboxTransferTicket Issue(string accountId, WorldId originWorldId, WorldId targetWorldId, Guid? sscLeaseId = null);

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


public sealed record SandboxSscStateSnapshot(
    string AccountId,
    WorldId ScopeWorldId,
    long Revision,
    string Payload,
    DateTimeOffset UpdatedAt);

public enum SandboxSscWriteStatus
{
    Applied,
    Conflict,
    Rejected
}

public sealed record SandboxSscWriteResult(
    SandboxSscWriteStatus Status,
    SandboxSscStateSnapshot? State = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public bool IsSuccess => Status == SandboxSscWriteStatus.Applied;
}

public sealed record SandboxSscTransferLease(
    Guid LeaseId,
    string AccountId,
    WorldId OriginWorldId,
    WorldId TargetWorldId,
    SandboxSscMode OriginMode,
    SandboxSscMode TargetMode,
    DateTimeOffset ExpiresAt);

public sealed record SandboxSscTransferPreparation(
    bool Accepted,
    SandboxSscTransferLease? Lease = null,
    string? ErrorCode = null,
    string? Message = null);

public interface ISandboxSscStore
{
    bool Supports(SandboxSscMode mode);

    SandboxSscStateSnapshot? Read(string accountId, WorldId worldId, SandboxSscMode mode);

    SandboxSscWriteResult Save(
        string accountId,
        WorldId worldId,
        SandboxSscMode mode,
        string payload,
        long expectedRevision,
        DateTimeOffset now);

    /// <summary>Periodic checkpoint; implementations may retain an identical revision after checking expectedRevision.</summary>
    SandboxSscWriteResult SaveCheckpoint(string accountId, WorldId worldId, SandboxSscMode mode, string payload,
        long expectedRevision, DateTimeOffset now) => Save(accountId, worldId, mode, payload, expectedRevision, now);

    SandboxSscTransferPreparation PrepareTransfer(
        string accountId,
        WorldId originWorldId,
        WorldId targetWorldId,
        SandboxSscMode originMode,
        SandboxSscMode targetMode,
        DateTimeOffset now,
        TimeSpan lifetime);

    SandboxSscTransferPreparation StageTransferState(
        Guid leaseId,
        string accountId,
        string payload,
        DateTimeOffset now);

    SandboxSscTransferPreparation CommitTransfer(
        Guid leaseId,
        string accountId,
        WorldId receivingWorldId,
        DateTimeOffset now);

    int DeleteWorldState(WorldId worldId);
}

public sealed record SandboxSscCharacterCaptureResult(
    bool Succeeded,
    string? Payload = null,
    string? ErrorCode = null);

public sealed record SandboxSscCharacterApplyResult(
    bool Succeeded,
    string? ErrorCode = null);

public interface ISandboxSscCharacterAdapter
{
    ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(
        PlayerId playerId,
        string payload,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        CaptureAsync(player.Id, cancellationToken);

    /// <summary>Returns the initial connection character, preferring an earlier native character-received snapshot where available.</summary>
    ValueTask<SandboxSscCharacterCaptureResult> CaptureJoinAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        CaptureAsync(player, cancellationToken);

    ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(PlayerSnapshot player, string payload, CancellationToken cancellationToken = default) =>
        ApplyAsync(player.Id, payload, cancellationToken);

    /// <summary>Produces a server-owned starter character, never an import of a client character.</summary>
    ValueTask<SandboxSscCharacterCaptureResult> CreateInitialAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SandboxSscCharacterCaptureResult(false, ErrorCode: "ssc.initial_unavailable"));

    ValueTask<SandboxSscCharacterCaptureResult> CaptureAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SandboxSscCharacterCaptureResult(false, ErrorCode: "ssc.authenticated_capture_unavailable"));

    ValueTask<SandboxSscCharacterApplyResult> ApplyAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, string payload, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SandboxSscCharacterApplyResult(false, "ssc.authenticated_apply_unavailable"));
}

public sealed record SandboxTransferAdmissionResult(
    bool Accepted,
    string? ErrorCode = null,
    string? Message = null,
    SandboxTransferTicket? Ticket = null);

public interface ISandboxTransferAdmissionService
{
    SandboxTransferAdmissionResult Register(
        SandboxTransferTicket ticket,
        string effectiveEndpoint,
        string clientUuid,
        DateTimeOffset now);

    SandboxTransferAdmissionResult Consume(
        WorldId receivingWorldId,
        string effectiveEndpoint,
        string clientUuid,
        DateTimeOffset now);
}

public sealed record SandboxPlayerTransferResult(bool Applied, string? ErrorCode = null);

public interface ISandboxPlayerTransferBoundary
{
    ValueTask<SandboxPlayerTransferResult> TransferAsync(
        PlayerId playerId,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket,
        CancellationToken cancellationToken = default);

    ValueTask<SandboxPlayerTransferResult> TransferAsync(PlayerSnapshot player, SandboxEndpoint endpoint, SandboxTransferTicket ticket, CancellationToken cancellationToken = default) =>
        TransferAsync(player.Id, endpoint, ticket, cancellationToken);
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
