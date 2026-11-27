using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands;

public sealed class CommandDispatcher : ICommandDispatcher, ICommandRegistry, ICommandCatalog, ICommandCompletionObservations
{
    public event Action<CommandCompletedObservation>? Completed;
    private readonly object sync = new();
    private IReadOnlyDictionary<string, ICommandHandler> handlers;

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
        !string.IsNullOrWhiteSpace(name) && Volatile.Read(ref handlers).ContainsKey(name);

    public IReadOnlyList<CommandDescriptor> SnapshotCommands() => Array.AsReadOnly(
        Volatile.Read(ref handlers)
            .Values
            .Select(static handler => handler.Descriptor)
            .DistinctBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public IDisposable Register(ICommandHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (sync)
        {
            var updated = new Dictionary<string, ICommandHandler>(handlers, StringComparer.OrdinalIgnoreCase);
            Register(updated, handler.Descriptor.Name, handler);
            foreach (string alias in handler.Descriptor.Aliases)
            {
                Register(updated, alias, handler);
            }

            Volatile.Write(ref handlers, updated);
        }

        return new Registration(this, handler);
    }

    public ValueTask<CommandExecutionResult> DispatchAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!Volatile.Read(ref handlers).TryGetValue(invocation.Name, out ICommandHandler? handler))
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

        if (Completed is null) return handler.ExecuteAsync(invocation, cancellationToken);
        var player = invocation.Principal as IPlayerCommandPrincipal;
        if (!invocation.Principal.IsServer && player?.SessionId is null)
            return handler.ExecuteAsync(invocation, cancellationToken);
        var observation = new CommandCompletedObservation(invocation.WorldId, descriptor.Name,
            player?.PlayerId, player?.SessionId, invocation.Principal.IsServer, default);
        ValueTask<CommandExecutionResult> pending = handler.ExecuteAsync(invocation, cancellationToken);
        if (!pending.IsCompletedSuccessfully) return ObserveCompletionAsync(pending, observation);
        CommandExecutionResult result = pending.Result;
        NotifyCompleted(observation with { Status = result.Status });
        return ValueTask.FromResult(result);
    }

    private async ValueTask<CommandExecutionResult> ObserveCompletionAsync(ValueTask<CommandExecutionResult> pending,
        CommandCompletedObservation observation)
    {
        CommandExecutionResult result = await pending.ConfigureAwait(false);
        NotifyCompleted(observation with { Status = result.Status });
        return result;
    }

    private void NotifyCompleted(CommandCompletedObservation observation)
    {
        if (Completed is not { } handlers) return;
        foreach (Action<CommandCompletedObservation> handler in handlers.GetInvocationList())
        {
            try { handler(observation); }
            catch (Exception) { global::System.Diagnostics.Trace.TraceWarning("Command observation callback failed."); }
        }
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

    private void Unregister(ICommandHandler handler)
    {
        lock (sync)
        {
            var updated = new Dictionary<string, ICommandHandler>(handlers, StringComparer.OrdinalIgnoreCase);
            foreach (string name in updated
                .Where(pair => ReferenceEquals(pair.Value, handler))
                .Select(static pair => pair.Key)
                .ToArray())
            {
                updated.Remove(name);
            }

            Volatile.Write(ref handlers, updated);
        }
    }

    private sealed class Registration(CommandDispatcher owner, ICommandHandler handler) : IDisposable
    {
        private CommandDispatcher? owner = owner;
        private ICommandHandler? handler = handler;

        public void Dispose()
        {
            CommandDispatcher? currentOwner = Interlocked.Exchange(ref owner, null);
            ICommandHandler? currentHandler = Interlocked.Exchange(ref handler, null);
            if (currentOwner is not null && currentHandler is not null)
            {
                currentOwner.Unregister(currentHandler);
            }
        }
    }
}
