namespace Vega.Contracts.Accounts;

public interface IAccountService
{
    IReadOnlyList<AccountSnapshot> SnapshotAccounts();

    ValueTask<AccountSnapshot?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    ValueTask<AccountSnapshot?> FindByIdAsync(AccountId id, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SnapshotAccounts().FirstOrDefault(account => account.Id == id));

    ValueTask<AccountMutationResult> CreateAsync(
        string name,
        string password,
        string groupName,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> DeleteAsync(
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> SetGroupAsync(
        string name,
        string groupName,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> SetCultureAsync(
        string name,
        string? culture,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> ChangePasswordAsync(
        string name,
        string password,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> RecordLoginAsync(
        string name,
        DateTimeOffset seenAt,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<AccountMutationResult> RecordLoginWithConnectionAsync(string name, DateTimeOffset seenAt,
        long expectedRevision, string actor, string? ip, string? uuid, CancellationToken cancellationToken = default) =>
        RecordLoginAsync(name, seenAt, expectedRevision, actor, cancellationToken);

    ValueTask<bool> VerifyPasswordAsync(
        string name,
        string password,
        CancellationToken cancellationToken = default);
}

public sealed record AccountMutationResult(
    bool Succeeded,
    string Code,
    AccountSnapshot? Before = null,
    AccountSnapshot? After = null);

/// <summary>Committed account mutations; observers must not wait for queued work or reenter the writer.</summary>
public interface IAccountChangeSource
{
    event Action<AccountChange>? AccountChanged;
}

public sealed record AccountChange(string Operation, string Actor, AccountSnapshot? Before, AccountSnapshot? After);
