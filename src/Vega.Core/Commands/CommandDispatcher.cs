using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands;

public sealed class CommandDispatcher :
    ICommandDispatcher,
    ICommandRegistry,
    ICommandCatalog,
    ICommandRegistrationCatalog,
    IOperationsLogSource
{
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
            {
                Register(map, alias, handler);
            }
        }

        this.handlers = map;
    }

    public event Action<OperationsLogMessage>? LogProduced;

    public bool CanDispatch(string name) =>
        !string.IsNullOrWhiteSpace(name) && Volatile.Read(ref handlers).ContainsKey(name);

    public IReadOnlyList<CommandDescriptor> SnapshotCommands() => Array.AsReadOnly(
        UniqueHandlers()
            .Select(static handler => handler.Descriptor)
            .OrderBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public IReadOnlyList<CommandRegistrationSnapshot> SnapshotRegistrations() => Array.AsReadOnly(
        UniqueHandlers()
            .Select(static handler =>
            {
                Type type = handler.GetType();
                return new CommandRegistrationSnapshot(
                    handler.Descriptor,
                    type.Assembly.GetName().Name ?? "unknown",
                    type.FullName ?? type.Name);
            })
            .OrderBy(static registration => registration.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
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

    public async ValueTask<CommandExecutionResult> DispatchAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!Volatile.Read(ref handlers).TryGetValue(invocation.Name, out ICommandHandler? handler))
        {
            CommandExecutionResult result = CreateFailure(
                CommandExecutionStatus.Failed,
                "command.not_found",
                "Unknown command.");
            PublishLog(invocation.Name, invocation, result);
            return result;
        }

        CommandDescriptor descriptor = handler.Descriptor;
        if (!descriptor.AllowServer && invocation.Principal.IsServer)
        {
            CommandExecutionResult result = CreateFailure(
                CommandExecutionStatus.Forbidden,
                "command.server_not_allowed",
                "This command cannot be executed from the server console.");
            PublishLog(descriptor.Name, invocation, result);
            return result;
        }

        if (descriptor.Permissions.Count > 0 && !descriptor.Permissions.Any(invocation.Principal.HasPermission))
        {
            CommandExecutionResult result = CreateFailure(
                CommandExecutionStatus.Forbidden,
                "command.permission_denied",
                "You do not have permission to use this command.");
            PublishLog(descriptor.Name, invocation, result);
            return result;
        }

        try
        {
            CommandExecutionResult result = await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            PublishLog(descriptor.Name, invocation, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            PublishLog(
                descriptor.Name,
                invocation,
                new CommandExecutionResult(CommandExecutionStatus.Failed, "command.cancelled"));
            throw;
        }
        catch
        {
            PublishLog(
                descriptor.Name,
                invocation,
                new CommandExecutionResult(CommandExecutionStatus.Failed, "command.exception"));
            throw;
        }
    }

    private IEnumerable<ICommandHandler> UniqueHandlers() => Volatile.Read(ref handlers)
        .Values
        .DistinctBy(static handler => handler.Descriptor.Name, StringComparer.OrdinalIgnoreCase);

    private void PublishLog(
        string commandName,
        CommandInvocation invocation,
        CommandExecutionResult result)
    {
        OperationsLogLevel level = result.Status is CommandExecutionStatus.Forbidden or CommandExecutionStatus.Failed
            ? OperationsLogLevel.Warning
            : OperationsLogLevel.Information;
        string error = string.IsNullOrWhiteSpace(result.ErrorCode)
            ? string.Empty
            : $" error={EscapeLogValue(result.ErrorCode)}";
        var message = new OperationsLogMessage(
            level,
            "command",
            $"principal={EscapeLogValue(invocation.Principal.Name)} type={(invocation.Principal.IsServer ? "server" : "player")} command={EscapeLogValue(commandName)} status={result.Status}{error}");
        Publish(message);
    }

    private void Publish(OperationsLogMessage message)
    {
        Action<OperationsLogMessage>? subscribers = LogProduced;
        if (subscribers is null)
        {
            return;
        }

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<OperationsLogMessage>)subscriber)(message);
            }
            catch
            {
                // Observability must never change command execution semantics or block later observers.
            }
        }
    }

    private static string EscapeLogValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

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
        {
            throw new ArgumentException("Command names and aliases must not be empty.", nameof(name));
        }

        if (!handlers.TryAdd(name, handler))
        {
            throw new InvalidOperationException($"Command name or alias '{name}' is already registered.");
        }
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
