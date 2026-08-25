namespace Vega.Contracts.Players;

/// <summary>
/// Optional lifecycle-operation bridge for facades that already hold the basic player-operations capability.
/// The bridge never widens the implementation contract: hosts must explicitly implement
/// <see cref="IPlayerLifecycleOperations"/> before life-state mutations become available.
/// </summary>
public static class PlayerLifecycleOperationExtensions
{
    public static ValueTask<PlayerOperationResult> HealAsync(
        this IPlayerOperations operations,
        PlayerId id,
        int? amount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations is IPlayerLifecycleOperations lifecycle
            ? lifecycle.HealAsync(id, amount, cancellationToken)
            : ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
    }

    public static ValueTask<PlayerOperationResult> KillAsync(
        this IPlayerOperations operations,
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations is IPlayerLifecycleOperations lifecycle
            ? lifecycle.KillAsync(id, reason, cancellationToken)
            : ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
    }

    public static ValueTask<PlayerOperationResult> ApplyBuffAsync(
        this IPlayerOperations operations,
        PlayerId id,
        int buffType,
        int durationTicks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations is IPlayerLifecycleOperations lifecycle
            ? lifecycle.ApplyBuffAsync(id, buffType, durationTicks, cancellationToken)
            : ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
    }

    public static ValueTask<PlayerOperationResult> RespawnAsync(
        this IPlayerOperations operations,
        PlayerId id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations is IPlayerLifecycleOperations lifecycle
            ? lifecycle.RespawnAsync(id, cancellationToken)
            : ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
    }
}
