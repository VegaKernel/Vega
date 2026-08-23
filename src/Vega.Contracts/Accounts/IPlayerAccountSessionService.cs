using Vega.Contracts.Players;

namespace Vega.Contracts.Accounts;

public interface IPlayerAccountSessionService
{
    bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account);

    ValueTask<bool> SetCultureAsync(
        PlayerId playerId,
        string? culture,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes an account session from a server-authenticated transfer identity.
    /// Implementations must not treat arbitrary client input as trusted identity.
    /// </summary>
    ValueTask<bool> EstablishTrustedTransferAsync(
        PlayerId playerId,
        string accountId,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
}
