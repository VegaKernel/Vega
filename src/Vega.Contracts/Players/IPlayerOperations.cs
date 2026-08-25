namespace Vega.Contracts.Players;

/// <summary>
/// Authoritative player mutations. Implementations must marshal Terraria state changes to the game thread.
/// </summary>
public interface IPlayerOperations
{
    ValueTask<PlayerOperationResult> KickAsync(
        PlayerId id,
        string reason,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerOperationResult> TeleportAsync(
        PlayerId id,
        PlayerPosition destination,
        CancellationToken cancellationToken = default);
}
