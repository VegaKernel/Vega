using Vega.Contracts.Worlds;

namespace Vega.Contracts.Commands;

public sealed record CommandInvocation(
    string Name,
    IReadOnlyList<string> Arguments,
    ICommandPrincipal Principal,
    ICommandOutput Output,
    bool Silent = false)
{
    /// <summary>
    /// World in which this command is being executed. Legacy callers default to the primary world.
    /// </summary>
    public WorldId WorldId { get; init; } = WorldId.Primary;
}

public interface ICommandPrincipal
{
    string Name { get; }
    bool IsServer { get; }
    bool HasPermission(string permission);
}

public interface ICommandOutput
{
    ValueTask InfoAsync(string message, CancellationToken cancellationToken = default);
    ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default);
    ValueTask WarningAsync(string message, CancellationToken cancellationToken = default);
    ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default);
}
