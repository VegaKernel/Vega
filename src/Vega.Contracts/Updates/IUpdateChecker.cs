namespace TShockNG.Contracts.Updates;

public interface IUpdateChecker
{
    Task CheckAsync(CancellationToken cancellationToken = default);
}
