using Vega.Contracts.Modules;
using Vega.PluginSdk;

[assembly: VegaPluginApiGeneration(VegaPluginApiGenerationAttribute.Current)]

namespace Vega.PluginTemplate;

public sealed class HelloPlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.PluginTemplate",
        "1.0.0",
        ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register("hello", "vega.plugin-template.hello",
            command => PluginReply.Success($"Hello, {command.Principal.Name}!"), aliases: ["hi"]);
        plugin.Players.OnEnteredWorld(player => plugin.Players.Message(player, "Hello from Vega.PluginTemplate!"));
        plugin.Log.Info("Hello plugin configured.");
    }
}
