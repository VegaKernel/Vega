using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.PluginSdk;

namespace Vega.PluginTemplate;

public sealed class HelloPlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.PluginTemplate",
        "1.0.0",
        ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register(new HelloCommand());
        plugin.Players.OnJoined(player =>
            plugin.Players.Message(player.PlayerId, "Hello from Vega.PluginTemplate!"));
    }

    private sealed class HelloCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "hello",
            ["hi"],
            ["vega.plugin-template.hello"],
            Syntax: "hello");

        public async ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            await invocation.Output.SuccessAsync("Hello from Vega!", cancellationToken);
            return CommandExecutionResult.Success;
        }
    }
}
