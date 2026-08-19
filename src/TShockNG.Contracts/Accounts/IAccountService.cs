namespace TShockNG.Contracts.Accounts;

public interface IAccountService
{
    ValueTask<AccountSnapshot?> FindByNameAsync(
        string name,
        CancellationToken cancellationToken = default);
}
