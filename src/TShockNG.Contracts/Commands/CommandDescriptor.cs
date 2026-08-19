namespace TShockNG.Contracts.Commands;

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
    string? ErrorCode = null)
{
    public static CommandExecutionResult Success => new(CommandExecutionStatus.Success);
}
