namespace Vega.Contracts.Players;

public interface IPlayerVitalOperations
{
    ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default);

    // Player life and respawn transitions are owned by the vanilla client/server protocol.
    ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.operation_unsupported"));
    ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot player, int damage, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.operation_unsupported"));
    ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.operation_unsupported"));
}
