using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Runtime;
using Vega.Core.Configuration;

namespace Vega.Core.Commands.Administration;

public sealed class ConfigurationCommand(
    VegaServerConfigurationState configuration,
    IServerReloadService reload) : ICommandHandler
{
    private readonly VegaServerConfigurationState configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly IServerReloadService reload = reload ?? throw new ArgumentNullException(nameof(reload));

    public CommandDescriptor Descriptor { get; } = new(
        "config",
        ["configuration", "reload"],
        ["vega.config.manage"],
        Syntax: "config show | config validate | config reload | reload");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (string.Equals(invocation.Name, "reload", StringComparison.OrdinalIgnoreCase))
        {
            return invocation.Arguments.Count == 0
                ? await ReloadAsync(invocation, cancellationToken).ConfigureAwait(false)
                : InvalidReloadUsage();
        }

        if (invocation.Arguments.Count == 0)
        {
            return Show(configuration.Current);
        }

        if (invocation.Arguments.Count != 1)
        {
            return InvalidUsage();
        }

        return invocation.Arguments[0].ToLowerInvariant() switch
        {
            "show" => Show(configuration.Current),
            "validate" => Validate(),
            "reload" => await ReloadAsync(invocation, cancellationToken).ConfigureAwait(false),
            _ => InvalidUsage()
        };
    }

    private CommandExecutionResult Validate()
    {
        try
        {
            _ = configuration.Validate();
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.config.valid")
                    .WithDefaultText("Configuration is valid."));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return CommandExecutionResult.Failed(
                "config.invalid",
                MessageDescriptor.Error("command.config.invalid")
                    .WithDefaultText(ex.Message));
        }
    }

    private async ValueTask<CommandExecutionResult> ReloadAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        ServerReloadResult result = await reload.ReloadAsync(
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);

        if (result.Status == ServerReloadStatus.Succeeded)
        {
            MessageDescriptor message = result.ErrorCode is null
                ? MessageDescriptor.Success("command.reload.completed")
                    .WithDefaultText(result.Message)
                : MessageDescriptor.Warning("command.reload.completed_with_warning")
                    .WithDefaultText(result.Message);
            return CommandExecutionResult.WithMessages(message);
        }

        string errorCode = result.ErrorCode ??
            (result.Status == ServerReloadStatus.Cancelled ? "reload.cancelled" : "reload.failed");
        return CommandExecutionResult.Failed(
            errorCode,
            MessageDescriptor.Error(result.Status == ServerReloadStatus.Cancelled
                    ? "command.reload.cancelled"
                    : "command.reload.failed")
                .WithDefaultText(result.Message));
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

    private static CommandExecutionResult InvalidReloadUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.invalid_usage",
        [MessageDescriptor.Error("command.reload.usage")
            .WithDefaultText("Usage: reload")]);
}
