namespace Vega.Contracts.Runtime;

/// <summary>
/// Performs process-level server operations without exposing Terraria runtime types.
/// </summary>
public interface IServerControl
{
    ValueTask SaveAsync(CancellationToken cancellationToken = default);

    ValueTask ShutdownAsync(
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the server without requesting a final world save.
    /// Implementations that cannot guarantee no-save shutdown should fail instead of silently saving.
    /// </summary>
    ValueTask ShutdownWithoutSaveAsync(
        string reason,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server-control implementation does not support no-save shutdown.");

    /// <summary>
    /// Replaces the password used for subsequent client admission without exposing runtime globals.
    /// The supplied secret must never be copied into diagnostics or result text.
    /// </summary>
    ValueTask SetPasswordAsync(
        string password,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This server-control implementation does not support live password changes.");
}
