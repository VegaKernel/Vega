namespace TShockNG.Contracts.Players;

/// <summary>
/// Sends user-visible messages without granting callers arbitrary access to Terraria player state.
/// </summary>
public interface IPlayerMessenger
{
    ValueTask<PlayerOperationResult> SendMessageAsync(
        PlayerId id,
        string message,
        CancellationToken cancellationToken = default);
}
