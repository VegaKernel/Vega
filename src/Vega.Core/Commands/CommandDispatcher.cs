using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands;

public sealed class CommandDispatcher : ICommandDispatcher, ICommandRegistry, ICommandCatalog, ICommandCompletionObservations, ICommandPolicyEvents
{
    public event Action<CommandCompletedObservation>? Completed;
    public event Func<PlayerCommandPolicyContext, CommandPolicyDecision>? BeforePlayerCommand;
    private readonly object sync = new();
    private IReadOnlyDictionary<string, ICommandHandler> handlers;

    public CommandDispatcher(IEnumerable<ICommandHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        var map = new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase);
        RegisterHandlers(map, handlers);

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

    public IDisposable Register(ICommandHandler handler) => RegisterBatch([handler]);

    /// <summary>Publish a whole command family atomically; one copy-on-write map and one lifetime handle.</summary>
    public IDisposable RegisterBatch(IEnumerable<ICommandHandler> commandFamily)
    {
        ArgumentNullException.ThrowIfNull(commandFamily);
        ICommandHandler[] batch = commandFamily.ToArray();

        lock (sync)
        {
            var updated = new Dictionary<string, ICommandHandler>(handlers, StringComparer.OrdinalIgnoreCase);
            RegisterHandlers(updated, batch);
            Volatile.Write(ref handlers, updated);
        }

        return new Registration(this, batch);
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

        // Player-only restrictive policy, after normal authorization. The callback sees
        // names and exact session identity, never arguments, output, or the principal.
        if (BeforePlayerCommand is { } policies && !invocation.Principal.IsServer &&
            invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } policyPlayer)
        {
            var context = new PlayerCommandPolicyContext(invocation.WorldId, descriptor.Name, invocation.Name, policyPlayer.PlayerId, session);
            foreach (Func<PlayerCommandPolicyContext, CommandPolicyDecision> policy in policies.GetInvocationList())
            {
                CommandPolicyDecision decision;
                try { decision = policy(context); }
                catch (Exception) { global::System.Diagnostics.Trace.TraceWarning("Command policy callback failed."); continue; }
                if (!decision.Rejected) continue;
                string reason = decision.Reason is { Length: > 0 and <= 256 } candidate && !candidate.Any(char.IsControl)
                    ? candidate : "Command rejected by server policy.";
                return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.Forbidden, "command.policy_rejected",
                    [MessageDescriptor.Error("command.policy_rejected", reason).WithDefaultText("{0}")]));
            }
            // A policy callback may have yielded the authority held by this principal.
            // Never execute using a permission check made before the callback invoked.
            if (descriptor.Permissions.Count > 0 && !descriptor.Permissions.Any(invocation.Principal.HasPermission))
                return ValueTask.FromResult(CreateFailure(CommandExecutionStatus.Forbidden, "command.permission_denied", "You do not have permission to use this command."));
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

    private static void RegisterHandlers(IDictionary<string, ICommandHandler> map, IEnumerable<ICommandHandler> commandFamily)
    {
        foreach (ICommandHandler handler in commandFamily)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Register(map, handler.Descriptor.Name, handler);
            foreach (string alias in handler.Descriptor.Aliases) Register(map, alias, handler);
        }
    }

    private void Unregister(ICommandHandler[] commandFamily)
    {
        var removing = new HashSet<ICommandHandler>(commandFamily, ReferenceEqualityComparer.Instance);
        lock (sync)
        {
            var updated = new Dictionary<string, ICommandHandler>(handlers, StringComparer.OrdinalIgnoreCase);
            foreach (string name in updated
                .Where(pair => removing.Contains(pair.Value))
                .Select(static pair => pair.Key)
                .ToArray())
            {
                updated.Remove(name);
            }

            Volatile.Write(ref handlers, updated);
        }
    }

    private sealed class Registration(CommandDispatcher owner, ICommandHandler[] commandFamily) : IDisposable
    {
        private ICommandHandler[]? family = commandFamily;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref family, null) is { } current) owner.Unregister(current);
        }
    }
}
