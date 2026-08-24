using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.PluginSdk;

namespace Vega.HotReload.TestPlugin;

public sealed class TestHotModule : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Test.HotPlugin",
        "1.0.0",
        ["Vega.Contracts"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register(new ProbeCommand());
    }

    private sealed class ProbeCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("hotprobe", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }
}
