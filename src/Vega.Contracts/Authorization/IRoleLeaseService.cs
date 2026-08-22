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
