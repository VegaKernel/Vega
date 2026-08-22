using Vega.Contracts.Players;

namespace Vega.Contracts.Accounts;

public interface IPlayerAccountSessionService
{
    bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account);

    ValueTask<bool> SetCultureAsync(
        PlayerId playerId,
        string? culture,
        CancellationToken cancellationToken = default);
}
