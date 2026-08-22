using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Core.Configuration;

namespace Vega.Core.Commands.Administration;

public sealed class ConfigurationCommand(VegaServerConfigurationState configuration) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "config",
        ["configuration"],
        ["vega.config.manage"]);

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        string action = invocation.Arguments.Count == 0
            ? "show"
            : invocation.Arguments[0].ToLowerInvariant();
        return ValueTask.FromResult(action switch
        {
            "show" => Show(configuration.Current),
            "validate" => Validate(),
            "reload" => Validate(reloadRequested: true),
            _ => InvalidUsage()
        });
    }

    private CommandExecutionResult Validate(bool reloadRequested = false)
    {
        try
        {
            _ = configuration.Validate();
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.config.valid")
                    .WithDefaultText(reloadRequested
                        ? "Configuration is valid. Server/listener/world settings are restart-only; no live state was changed."
                        : "Configuration is valid."));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CommandExecutionResult.Failed(
                "config.invalid",
                MessageDescriptor.Error("command.config.invalid")
                    .WithDefaultText(ex.Message));
        }
    }

    private static CommandExecutionResult Show(EffectiveVegaServerConfiguration effective) =>
        CommandExecutionResult.WithMessages(effective.Values
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => MessageDescriptor.Info(
                    "command.config.value",
                    pair.Key,
                    pair.Value.DisplayValue,
                    pair.Value.Source)
                .WithDefaultText("{0} = {1} ({2})"))
            .ToArray());

    private static CommandExecutionResult InvalidUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.invalid_usage",
        [MessageDescriptor.Error("command.config.usage")
            .WithDefaultText("Usage: config show | config validate | config reload")]);
}
