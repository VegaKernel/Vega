using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Core.Configuration;
using Vega.Contracts.Worlds;
using System.Text.Json;

namespace Vega.Core.Commands.Administration;

public sealed class ConfigurationCommand(VegaServerConfigurationState configuration, ReloadConfigurationCommand? reload = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "config",
        ["configuration"],
        ["vega.config.manage"],
        Syntax: "config show | config validate | config reload");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        string action = invocation.Arguments.Count == 0
            ? "show"
            : invocation.Arguments[0].ToLowerInvariant();
        if (invocation.Arguments.Count > 1) return ValueTask.FromResult(InvalidUsage());
        if (action == "reload" && reload is not null)
            return reload.ExecuteAsync(invocation with { Arguments = [] }, cancellationToken);
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

/// <summary>Reloads independent world-local policies without replacing listener or native world state.</summary>
public sealed class ReloadConfigurationCommand(
    VegaServerConfigurationState configuration,
    WorldId worldId,
    IReadOnlyList<(string Name, Func<CancellationToken, ValueTask<bool>> Reload)> policies) : ICommandHandler
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public CommandDescriptor Descriptor { get; } = new("reload", [], ["vega.config.manage"], Syntax: "reload");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
            return new(CommandExecutionStatus.InvalidUsage, "command.invalid_usage",
                [MessageDescriptor.Error("command.reload.usage").WithDefaultText("Usage: reload")]);
        if (invocation.WorldId != worldId) return CommandExecutionResult.Failed("config.wrong_world");
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return CommandExecutionResult.Failed("config.reload.busy", MessageDescriptor.Error("config.reload.busy").WithDefaultText("Reload already in progress."));
        try
        {
            try { await Task.Run(configuration.Validate, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return CommandExecutionResult.Failed("config.invalid", MessageDescriptor.Error("command.config.invalid")
                    .WithDefaultText("Server configuration is invalid; no policy was reloaded."));
            }

            var messages = new List<MessageDescriptor>();
            bool failed = false;
            foreach (var policy in policies)
            {
                try
                {
                    bool loaded = await policy.Reload(cancellationToken).ConfigureAwait(false);
                    messages.Add(MessageDescriptor.Info("config.reload.policy", policy.Name).WithDefaultText(loaded
                        ? "{0}: reloaded." : "{0}: file absent; current policy retained."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    failed = true;
                    messages.Add(MessageDescriptor.Error("config.reload.policy_failed", policy.Name)
                        .WithDefaultText("{0}: invalid, unreadable or busy; last valid state was not overwritten."));
                }
            }
            messages.Add(MessageDescriptor.Info("config.reload.restart_only")
                .WithDefaultText("Server/listener/world startup configuration is restart-only. Policies are applied independently."));
            return new(failed ? CommandExecutionStatus.Failed : CommandExecutionStatus.Success,
                failed ? "config.reload.partial" : null, messages);
        }
        finally { gate.Release(); }
    }
}
