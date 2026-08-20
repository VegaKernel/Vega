using TShockNG.Contracts.Commands;

namespace TShockNG.Contracts.Operations;

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
