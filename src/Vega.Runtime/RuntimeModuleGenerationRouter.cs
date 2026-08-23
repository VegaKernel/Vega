using Multiplicity.Packets.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;

namespace Vega.Runtime;

internal sealed record RuntimeModuleGeneration(
    IPacketProcessor<PacketView>? Pvp,
    IChatRecipientRenderer? Mentions,
    IChestInteractionPolicy? Chests,
    IWorldCleanupStatusReader? Cleanup,
    IReadOnlyDictionary<string, ICommandHandler> Commands);

internal sealed class RuntimeModuleGenerationRouter :
    IPacketProcessor<PacketView>,
    IChatRecipientRenderer,
    IChestInteractionPolicy,
    IWorldCleanupStatusReader
{
    private RuntimeModuleGeneration active = new(null, null, null, null,
        new Dictionary<string, ICommandHandler>(StringComparer.OrdinalIgnoreCase));
    private SoftRestartCoordinator? coordinator;

    public int Order => 100;

    public void Attach(SoftRestartCoordinator value) =>
        coordinator = value ?? throw new ArgumentNullException(nameof(value));

    public void Activate(RuntimeModuleGeneration generation) =>
        Volatile.Write(ref active, generation ?? throw new ArgumentNullException(nameof(generation)));

    public IDisposable RegisterCommands(ICommandRegistry registry)
    {
        ICommandHandler[] handlers = Volatile.Read(ref active).Commands.Values
            .DistinctBy(static handler => handler.Descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .Select(handler => (ICommandHandler)new RoutedCommand(this, handler.Descriptor))
            .ToArray();
        return new RegistrationScope(handlers.Select(registry.Register).ToArray());
    }

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (!TryAcquire(out IModuleCallbackLease? lease))
        {
            return PacketProcessingDecision.Reject("module-restart-in-progress");
        }

        using (lease)
        {
            IPacketProcessor<PacketView>? processor = Volatile.Read(ref active).Pvp;
            return processor?.Process(in context) ?? PacketProcessingDecision.Allow;
        }
    }

    public ChatRecipientRenderResult Render(
        ChatMessageContext context,
        PlayerSnapshot recipient,
        string content)
    {
        if (!TryAcquire(out IModuleCallbackLease? lease))
        {
            return new ChatRecipientRenderResult(content);
        }

        using (lease)
        {
            IChatRecipientRenderer? renderer = Volatile.Read(ref active).Mentions;
            return renderer?.Render(context, recipient, content) ?? new ChatRecipientRenderResult(content);
        }
    }

    public async ValueTask ObserveAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!TryAcquire(out IModuleCallbackLease? lease))
        {
            return;
        }

        using (lease)
        {
            IChestInteractionPolicy? policy = Volatile.Read(ref active).Chests;
            if (policy is not null)
            {
                await policy.ObserveAsync(identity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public WorldCleanupStatusSnapshot Read()
    {
        if (!TryAcquire(out IModuleCallbackLease? lease))
        {
            return WorldCleanupStatusSnapshot.Disabled;
        }

        using (lease)
        {
            return Volatile.Read(ref active).Cleanup?.Read() ?? WorldCleanupStatusSnapshot.Disabled;
        }
    }

    private bool TryAcquire(out IModuleCallbackLease? lease)
    {
        SoftRestartCoordinator? value = coordinator;
        if (value is null)
        {
            lease = null;
            return false;
        }

        return value.TryAcquireCallback(out lease);
    }

    private async ValueTask<CommandExecutionResult> ExecuteAsync(
        string name,
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!TryAcquire(out IModuleCallbackLease? lease))
        {
            return Failed("command.softrestart.busy", "Module restart is in progress; retry the command.");
        }

        using (lease)
        {
            if (!Volatile.Read(ref active).Commands.TryGetValue(name, out ICommandHandler? handler))
            {
                return Failed("command.module.unavailable", "The module command is unavailable in this generation.");
            }

            return await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static CommandExecutionResult Failed(string code, string text) =>
        new(CommandExecutionStatus.Failed, code,
            [MessageDescriptor.Error(code).WithDefaultText(text)]);

    private sealed class RoutedCommand(RuntimeModuleGenerationRouter owner, CommandDescriptor descriptor)
        : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = descriptor;

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            owner.ExecuteAsync(Descriptor.Name, invocation, cancellationToken);
    }

    private sealed class RegistrationScope(IDisposable[] registrations) : IDisposable
    {
        private IDisposable[]? registrations = registrations;

        public void Dispose()
        {
            IDisposable[]? owned = Interlocked.Exchange(ref registrations, null);
            if (owned is null)
            {
                return;
            }

            for (int index = owned.Length - 1; index >= 0; index--)
            {
                owned[index].Dispose();
            }
        }
    }
}
