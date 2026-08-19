namespace TShockNG.Contracts.Commands;

public sealed record CommandInvocation(
    string Name,
    IReadOnlyList<string> Arguments,
    ICommandPrincipal Principal,
    ICommandOutput Output,
    bool Silent = false);

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
