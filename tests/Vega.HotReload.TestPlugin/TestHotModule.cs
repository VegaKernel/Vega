using Vega.Contracts.Commands;
using Vega.Contracts.Modules;

namespace Vega.HotReload.TestPlugin;

public sealed class TestHotModule : IHotReloadableVegaModule
{
    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Test.HotPlugin",
        "1.0.0",
        ["Vega.Contracts"]);

    public ValueTask StartAsync(IHotModuleContext context, CancellationToken cancellationToken)
    {
        context.RegisterCommand(new ProbeCommand());
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ProbeCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("hotprobe", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }
}
