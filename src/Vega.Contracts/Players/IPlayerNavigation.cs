using Vega.Contracts.Accounts;

namespace Vega.Contracts.Players;

/// <summary>Session-bound navigation. Implementations resolve live players and destinations on the game thread.</summary>
public interface IPlayerNavigation
{
    ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default);
    /// <summary>Private saved positions must revalidate the authentication tenure and physical world on the authoritative thread.</summary>
    ValueTask<PlayerOperationResult> ToAuthenticatedPositionAsync(PlayerSnapshot player, PlayerPosition destination,
        Guid worldFileId, IPlayerAccountSessionService accounts, PlayerAccountBinding binding, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.teleport.authentication_boundary_unavailable"));
    ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
}
