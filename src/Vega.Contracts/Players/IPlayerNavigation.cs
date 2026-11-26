namespace Vega.Contracts.Players;

/// <summary>Session-bound navigation. Implementations resolve live players and destinations on the game thread.</summary>
public interface IPlayerNavigation
{
    ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default);
}
