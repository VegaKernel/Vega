namespace Vega.Contracts.Accounts;

public interface IAccountService
{
    IReadOnlyList<AccountSnapshot> SnapshotAccounts();

    ValueTask<AccountSnapshot?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

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
