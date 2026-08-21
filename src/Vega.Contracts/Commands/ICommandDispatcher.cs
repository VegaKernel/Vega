namespace Vega.Contracts.Commands;

/// <summary>
/// Dispatches commands through the modern command pipeline.
/// </summary>
public interface ICommandCatalog
{
    /// <summary>
    /// Returns a stable snapshot of the commands currently registered in the dispatcher.
    /// Dynamic module registrations are reflected by the next snapshot.
    /// </summary>
    IReadOnlyList<CommandDescriptor> SnapshotCommands();
}

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
/// Owns command registrations whose lifetime can be shorter than the server process.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>
    /// Registers a handler until the returned lease is disposed.
    /// </summary>
    IDisposable Register(ICommandHandler handler);
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
