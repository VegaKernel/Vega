using Vega.Contracts.Commands;

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
