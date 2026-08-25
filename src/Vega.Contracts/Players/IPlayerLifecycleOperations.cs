namespace Vega.Contracts.Players;

/// <summary>
/// Authoritative life-state mutations layered on top of basic player operations.
/// Consumers should request this capability only when they need to change health, buffs or death state.
/// </summary>
public interface IPlayerLifecycleOperations : IPlayerOperations
{
    /// <summary>
    /// Restores life to a living player. A null amount restores the player to the current maximum life.
    /// </summary>
    ValueTask<PlayerOperationResult> HealAsync(
        PlayerId id,
        int? amount = null,
        CancellationToken cancellationToken = default);

    /// <summary>Kills a living player through Terraria's authoritative death path.</summary>
    ValueTask<PlayerOperationResult> KillAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Applies one Terraria buff for an explicit duration measured in game ticks.</summary>
    ValueTask<PlayerOperationResult> ApplyBuffAsync(
        PlayerId id,
        int buffType,
        int durationTicks,
        CancellationToken cancellationToken = default);

    /// <summary>Immediately revives a dead player through Terraria's normal respawn path.</summary>
    ValueTask<PlayerOperationResult> RespawnAsync(
        PlayerId id,
        CancellationToken cancellationToken = default);
}
