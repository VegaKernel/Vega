using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;

namespace Vega.Core.Commands.Administration;

public sealed class ServerPluginCommand(IServerPluginController plugins) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "plugin",
        ["plugins"],
        ["server.plugins.manage"],
        Syntax: "plugin [list] | plugin reload <name-or-dll> | plugin unload <name-or-dll>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 ||
            string.Equals(invocation.Arguments[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            return List();
        }

        if (invocation.Arguments.Count != 2)
        {
            return InvalidUsage();
        }

        ServerPluginOperationResult result;
        if (string.Equals(invocation.Arguments[0], "reload", StringComparison.OrdinalIgnoreCase))
        {
            result = await plugins.ReloadAsync(invocation.Arguments[1], cancellationToken);
        }
        else if (string.Equals(invocation.Arguments[0], "unload", StringComparison.OrdinalIgnoreCase))
        {
            result = await plugins.UnloadAsync(invocation.Arguments[1], cancellationToken);
        }
        else
        {
            return InvalidUsage();
        }

        MessageDescriptor message = result.Succeeded
            ? MessageDescriptor.Success("command.plugin.completed").WithDefaultText(result.Message)
            : MessageDescriptor.Error("command.plugin.failed").WithDefaultText(result.Message);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(message)
            : CommandExecutionResult.Failed("plugin.operation_failed", message);
    }

    private CommandExecutionResult List()
    {
        IReadOnlyList<ModuleOperationsSnapshot> modules = plugins.SnapshotModules();
        string mode = plugins.HotReloadEnabled ? "enabled" : "disabled";
        string summary = modules.Count == 0
            ? $"Plugin hot reload is {mode}; no hot-loadable plugins are registered."
            : $"Plugin hot reload is {mode}: " + string.Join(
                ", ",
                modules.Select(static module => $"{module.Name} {module.Version} [{module.State}]"));
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.plugin.list").WithDefaultText(summary));
    }

    private static CommandExecutionResult InvalidUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.invalid_usage",
        [MessageDescriptor.Error("command.plugin.usage")
            .WithDefaultText("Usage: plugin [list] | plugin reload <name-or-dll> | plugin unload <name-or-dll>")]);
}
