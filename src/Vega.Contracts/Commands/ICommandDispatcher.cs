namespace TShockNG.Contracts.Commands;

/// <summary>
/// Dispatches commands through the modern command pipeline.
/// </summary>
public interface ICommandDispatcher
{
    /// <summary>
    /// Returns whether a command name or alias is owned by the modern command pipeline.
    /// </summary>
    bool CanDispatch(string name);

    ValueTask<CommandExecutionResult> DispatchAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles one modern command.
/// </summary>
public interface ICommandHandler
{
    CommandDescriptor Descriptor { get; }

    ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken);
}
