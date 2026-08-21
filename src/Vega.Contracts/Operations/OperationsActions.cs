using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Operations;

public sealed record OperationsActionResult(
    CommandExecutionStatus Status,
    string? ErrorCode,
    IReadOnlyList<string> Messages)
{
    public bool IsSuccess => Status == CommandExecutionStatus.Success;
}

public interface IOperationsCommandService
{
    ValueTask<OperationsActionResult> ExecuteAsync(
        string commandText,
        CancellationToken cancellationToken = default);
}

public interface IUnhandledOperationsCommandSink
{
    ValueTask SubmitAsync(
        string commandText,
        CancellationToken cancellationToken = default);
}

public interface IOperationsWorldService
{
    ValueTask<OperationsActionResult> MovePlayerAsync(
        PlayerId playerId,
        WorldId targetWorldId,
        CancellationToken cancellationToken = default);
}
