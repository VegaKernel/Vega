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

    ValueTask ShutdownWithoutSaveAsync(string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Shutdown without save is unavailable."));

    ValueTask SetPasswordAsync(string password, CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new NotSupportedException("Runtime password changes are unavailable."));
}
