namespace Vega.Contracts.Bans;

public interface IBanService
{
    ValueTask<BanSnapshot?> FindActiveBanAsync(
        string identifier,
        CancellationToken cancellationToken = default);
}
