namespace Vega.Contracts.Players;

/// <summary>
/// Sends user-visible messages without granting callers arbitrary access to Terraria player state.
/// </summary>
public interface IPlayerMessenger
{
    /// <summary>True only when the snapshot overload revalidates the original session at delivery.</summary>
    bool SupportsSessionBoundMessages => false;

    ValueTask<PlayerOperationResult> SendMessageAsync(
        PlayerId id,
        string message,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default) =>
        SendMessageAsync(player.Id, message, cancellationToken);
}
