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
}
