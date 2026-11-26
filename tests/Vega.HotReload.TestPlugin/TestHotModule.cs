using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Scenes;
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
        plugin.Commands.Register(new FaultProbeCommand());
        if (plugin.World.FakeProvider.IsAvailable)
        {
            var provider = plugin.World.FakeProvider.CreateGlobal("reload-probe", new SceneBounds(10, 10, 2, 2),
                SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active)));
            if (plugin.World.FakeProvider.SupportsVirtualChests)
                provider.CreateVirtualChest(0, 0, "reload chest", [new(0, 1, 1, 0)]);
            plugin.World.FakeProvider.CreateVisibilityLayer("reload-visibility", []).AssignNpc(7);
        }
    }

    private sealed class ProbeCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("hotprobe", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }

    private sealed class FaultProbeCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("hotfault", [], []);
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Test-only plugin callback fault.");
    }
}
