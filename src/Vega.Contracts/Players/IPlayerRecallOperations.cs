namespace Vega.Contracts.Players;

/// <summary>
/// Authoritative player recall operations that use Terraria's own spawn-point resolution instead of exposing spawn fields.
/// </summary>
public interface IPlayerRecallOperations
{
    /// <summary>
    /// Recalls a living player to their personal spawn point through Terraria's normal recall path.
    /// </summary>
    ValueTask<PlayerOperationResult> RecallHomeAsync(
        PlayerId id,
        CancellationToken cancellationToken = default);
}
