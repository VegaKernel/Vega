using Vega.Contracts.Commands;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginCommands(HotModuleRegistrationScope owner, ICommandRegistry commands) : IPluginCommands
    {
        public bool CatalogAvailable => commands is ICommandCatalog;

        public IReadOnlyList<CommandDescriptor> Snapshot() => owner.lifetime.IsCancellationRequested ||
            commands is not ICommandCatalog catalog ? [] : Array.AsReadOnly(catalog.SnapshotCommands().Select(static descriptor => descriptor with
            {
                Aliases = Array.AsReadOnly(descriptor.Aliases.ToArray()),
                Permissions = Array.AsReadOnly(descriptor.Permissions.ToArray())
            }).ToArray());

        public void Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            owner.Track(RegisterCore(handler));
        }

        public IDisposable RegisterCore(ICommandHandler handler) =>
            commands.Register(new GuardedCommandHandler(owner, handler));

        private sealed class GuardedCommandHandler : ICommandHandler
        {
            private readonly HotModuleRegistrationScope owner;
            private readonly ICommandHandler inner;
            private readonly string operation;

            public GuardedCommandHandler(HotModuleRegistrationScope owner, ICommandHandler inner)
            {
                this.owner = owner;
                this.inner = inner;
                Descriptor = inner.Descriptor;
                operation = $"command:{Descriptor.Name}";
            }

            public CommandDescriptor Descriptor { get; }

            public ValueTask<CommandExecutionResult> ExecuteAsync(
                CommandInvocation invocation,
                CancellationToken cancellationToken) =>
                owner.executionGuard.InvokeCommandAsync(
                    operation,
                    inner,
                    invocation,
                    cancellationToken);
        }
    }
}
