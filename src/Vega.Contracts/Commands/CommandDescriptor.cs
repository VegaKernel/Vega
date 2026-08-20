using Vega.Contracts.Messaging;

namespace Vega.Contracts.Commands;

public sealed record CommandDescriptor(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Permissions,
    bool AllowServer = true);

public enum CommandExecutionStatus
{
    Success,
    InvalidUsage,
    Forbidden,
    Failed
}

public readonly record struct CommandExecutionResult(
    CommandExecutionStatus Status,
    string? ErrorCode = null,
    IReadOnlyList<MessageDescriptor>? Messages = null)
{
    public static CommandExecutionResult Success => new(CommandExecutionStatus.Success);

    public static CommandExecutionResult WithMessages(params MessageDescriptor[] messages) =>
        new(CommandExecutionStatus.Success, Messages: messages);

    public static CommandExecutionResult Failed(
        string errorCode,
        params MessageDescriptor[] messages) =>
        new(CommandExecutionStatus.Failed, errorCode, messages);
}
