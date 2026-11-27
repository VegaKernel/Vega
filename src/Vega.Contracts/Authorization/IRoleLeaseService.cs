using Vega.Contracts.Accounts;

namespace Vega.Contracts.Authorization;

public interface IRoleLeaseService
{
    IReadOnlyList<RoleLeaseSnapshot> SnapshotActive(DateTimeOffset now);

    RoleLeaseSnapshot? FindActive(AccountId accountId, DateTimeOffset now);

    ValueTask<RoleLeaseMutationResult> GrantAsync(
        AccountId accountId,
        string groupName,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        string actor,
        CancellationToken cancellationToken = default);

    /// <summary>Grant only if the caller's observed account revision and active lease still match.</summary>
    ValueTask<RoleLeaseMutationResult> GrantConditionalAsync(AccountId accountId, string groupName, DateTimeOffset issuedAt,
        DateTimeOffset expiresAt, string actor, long expectedAccountRevision, Guid? expectedActiveLeaseId, long? expectedActiveLeaseRevision,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(new RoleLeaseMutationResult(false, "role-lease.conditional-unavailable"));

    ValueTask<RoleLeaseMutationResult> RevokeAsync(
        Guid leaseId,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);
}

public sealed record RoleLeaseSnapshot(
    Guid Id,
    AccountId AccountId,
    string GroupName,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    long Revision,
    bool Revoked)
{
    public bool IsActive(DateTimeOffset now) => !Revoked && ExpiresAt > now;
}

public sealed record RoleLeaseMutationResult(
    bool Succeeded,
    string Code,
    RoleLeaseSnapshot? Before = null,
    RoleLeaseSnapshot? After = null);

public interface IRoleLeaseChangeSource
{
    event Action<RoleLeaseChange>? RoleLeaseChanged;
}

public sealed record RoleLeaseChange(string Operation, string Actor, RoleLeaseSnapshot? Before, RoleLeaseSnapshot? After);
