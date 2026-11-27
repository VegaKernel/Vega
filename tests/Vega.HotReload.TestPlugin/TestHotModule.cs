using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

namespace Vega.HotReload.TestPlugin;

public sealed class TestHotModule : VegaPlugin
{
    private string? stopGate;
    private string? startFailure;
    public override VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Test.HotPlugin",
        "1.0.0",
        ["Vega.Contracts"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register(new ProbeCommand());
        plugin.Commands.Register(new FaultProbeCommand());
        stopGate = Path.Combine(plugin.Storage.DataDirectory, "hold-stop");
        startFailure = Path.Combine(plugin.Storage.DataDirectory, "start-failure");
        plugin.Commands.Register(new StallProbeCommand(Path.Combine(plugin.Storage.DataDirectory, "hold-command")));
        string startGate = Path.Combine(plugin.Storage.DataDirectory, "hold-start-io");
        if (File.Exists(startGate))
            plugin.Tasks.RunIo("blocked-start-io", async _ =>
            {
                while (File.Exists(startGate)) await Task.Delay(10);
                return true;
            }, static _ => { });
        if (File.Exists(Path.Combine(plugin.Storage.DataDirectory, "soak-mode")))
        {
            plugin.Players.OnJoined(_ => plugin.Tasks.Post("faulty-joined", static () => { }));
            plugin.Players.OnLeft(_ => plugin.Tasks.Post("faulty-left", static () => { }));
            plugin.Tasks.Every("faulty-periodic", TimeSpan.FromMilliseconds(5), static () => { });
        }
        if (plugin.World.FakeProvider.IsAvailable)
        {
            var provider = plugin.World.FakeProvider.CreateGlobal("reload-probe", new SceneBounds(10, 10, 4, 2),
                SceneTileArea.Fill(4, 2, new SceneTile(21, Flags: SceneTileFlags.Active)));
            if (plugin.World.FakeProvider.SupportsVirtualChests)
                provider.CreateVirtualChest(0, 0, "reload chest", [new(0, 1, 1, 0)]);
            if (plugin.World.FakeProvider.SupportsVirtualSigns)
                provider.CreateVirtualSign(2, 0, "reload sign");
            plugin.World.FakeProvider.CreateVisibilityLayer("reload-visibility", []).AssignNpc(7);
        }
    }

    protected override ValueTask OnStartedAsync(CancellationToken cancellationToken) => File.Exists(startFailure)
        ? ValueTask.FromException(new InvalidOperationException("Test-only startup failure.")) : ValueTask.CompletedTask;

    protected override ValueTask OnStoppingAsync(CancellationToken cancellationToken)
    {
        // Test-only non-cooperative synchronous stop, released by the host fixture.
        string cancellationGate = stopGate + "-cancel";
        if (File.Exists(cancellationGate))
        {
            using var registration = cancellationToken.Register(() =>
            {
                while (File.Exists(cancellationGate)) Thread.Sleep(10);
            });
            while (File.Exists(cancellationGate)) Thread.Sleep(10);
        }
        while (File.Exists(stopGate)) Thread.Sleep(10);
        return ValueTask.CompletedTask;
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

    private sealed class StallProbeCommand(string gate) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("hotstall", [], []);
        public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            File.WriteAllText(gate + ".entered", "1");
            while (File.Exists(gate)) await Task.Delay(10);
            return CommandExecutionResult.Success;
        }
    }
}
