using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.PluginSdk;

[assembly: VegaPluginApiGeneration(VegaPluginApiGenerationAttribute.Current)]

namespace Vega.HotReload.HealthyTestPlugin;

public sealed class HealthyTestPlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } = new("Vega.Test.HealthyPlugin", "1.0.0", []);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register(new Probe());
        if (File.Exists(Path.Combine(plugin.Storage.DataDirectory, "soak-mode")))
        {
            plugin.Players.OnJoined(_ => plugin.Tasks.Post("healthy-joined", static () => { }));
            plugin.Players.OnLeft(_ => plugin.Tasks.Post("healthy-left", static () => { }));
            plugin.Tasks.Every("healthy-periodic", TimeSpan.FromMilliseconds(5), static () => { });
        }
    }

    private sealed class Probe : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("healthyprobe", [], []);
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }
}
