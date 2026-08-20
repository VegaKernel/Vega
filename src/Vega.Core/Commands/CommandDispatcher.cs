using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;

namespace TShockNG.Core.Commands;

public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly IReadOnlyDictionary<string, ICommandHandler> handlers;

    public CommandDispatcher(IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        foreach (ICommandHandler handler in handlers)
        {
            Register(map, handler.Descriptor.Name, handler);
            foreach (string alias in handler.Descriptor.Aliases)
                Register(map, alias, handler);
        }

        this.handlers = map;
    }

    public bool CanDispatch(string name) =>
        !string.IsNullOrWhiteSpace(name) && handlers.ContainsKey(name);

    public ValueTask<CommandExecutionResult> DispatchAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!handlers.TryGetValue(invocation.Name, out ICommandHandler? handler))
        {
            return ValueTask.FromResult(CreateFailure(
                CommandExecutionStatus.Failed,
                "command.not_found",
                "Unknown command."));
        }

        CommandDescriptor descriptor = handler.Descriptor;
        if (!descriptor.AllowServer && invocation.Principal.IsServer)
        {
            return ValueTask.FromResult(CreateFailure(
                CommandExecutionStatus.Forbidden,
                "command.server_not_allowed",
                "This command cannot be executed from the server console."));
        }

        if (descriptor.Permissions.Count > 0 && !descriptor.Permissions.Any(invocation.Principal.HasPermission))
        {
            return ValueTask.FromResult(CreateFailure(
                CommandExecutionStatus.Forbidden,
                "command.permission_denied",
                "You do not have permission to use this command."));
        }

        return handler.ExecuteAsync(invocation, cancellationToken);
    }

    private static CommandExecutionResult CreateFailure(
        CommandExecutionStatus status,
        string errorCode,
        string defaultText) =>
        new(
            status,
            errorCode,
            [MessageDescriptor.Error(errorCode).WithDefaultText(defaultText)]);

    private static void Register(
        IDictionary<string, ICommandHandler> handlers,
        string name,
        ICommandHandler handler)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Command names and aliases must not be empty.", nameof(name));

        if (!handlers.TryAdd(name, handler))
            throw new InvalidOperationException($"Command name or alias '{name}' is already registered.");
    }
}
