namespace Vega.Contracts.Accounts;

public enum AccountIdentityEvidenceKind
{
    ClientUuid,
    IpAddress
}

/// <summary>
/// Persisted observation that links an authenticated account to a client identity signal.
/// Values are administrative/sensitive data and must not be exposed through redacted snapshots.
/// </summary>
public sealed record AccountIdentityEvidenceSnapshot(
    AccountId AccountId,
    string AccountName,
    AccountIdentityEvidenceKind Kind,
    string Value,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    long ObservationCount);

public sealed record PossibleAccountLinkSnapshot(
    AccountId AccountId,
    string AccountName,
    int SharedSignals,
    bool SharedClientUuid,
    bool SharedIpAddress,
    DateTimeOffset LastMatchedAt);

public interface IAccountIdentityEvidenceService
{
    IReadOnlyList<AccountIdentityEvidenceSnapshot> SnapshotEvidence();

    IReadOnlyList<PossibleAccountLinkSnapshot> FindPossibleAccounts(AccountId accountId);

    ValueTask RecordAsync(
        AccountId accountId,
        string accountName,
        string? clientUuid,
        string? ipAddress,
        DateTimeOffset seenAt,
        CancellationToken cancellationToken = default);
}
