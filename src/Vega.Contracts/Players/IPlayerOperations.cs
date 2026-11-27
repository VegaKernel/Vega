namespace Vega.Contracts.Players;

/// <summary>
/// Authoritative player mutations. Implementations must marshal Terraria state changes to the game thread.
/// </summary>
public interface IPlayerOperations
{
    /// <summary>True only when the session-bound kick overload revalidates the original session at delivery.</summary>
    bool SupportsSessionBoundKicks => false;

    ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnect the original observed session rather than a later occupant of its slot.</summary>
    ValueTask<PlayerOperationResult> KickAsync(PlayerId id, PlayerSessionId sessionId, string reason,
        CancellationToken cancellationToken = default) => KickAsync(id, reason, cancellationToken);

    ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason,
        CancellationToken cancellationToken = default) => player.SessionId is { } session
        ? KickAsync(player.Id, session, reason, cancellationToken) : KickAsync(player.Id, reason, cancellationToken);

    ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> ResynchronizeAreaAsync(PlayerSnapshot player, int radius, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlayerOperationResult.Failed("player.sync_unsupported"));
}
