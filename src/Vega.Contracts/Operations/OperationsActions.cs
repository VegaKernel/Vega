using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Operations;

public enum OperationsCommandExitCode
{
    Success,
    ValidationError,
    Forbidden,
    Failed
}

public sealed record OperationsCommandResult(
    OperationsCommandExitCode ExitCode,
    string Message,
    string? ErrorCode = null);

public interface IOperationsCommandService
{
    ValueTask<OperationsCommandResult> ExecuteAsync(
        string commandLine,
        CancellationToken cancellationToken = default);
}

public interface IOperationsWorldService
{
    ValueTask<OperationsCommandResult> MovePlayerAsync(
        PlayerId playerId,
        WorldId targetWorldId,
        CancellationToken cancellationToken = default);
}
