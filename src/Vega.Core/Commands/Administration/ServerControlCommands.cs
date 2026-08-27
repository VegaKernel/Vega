using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Runtime;

namespace Vega.Core.Commands.Administration;

public sealed class SaveCommand(IServerControl server) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "save",
        Array.Empty<string>(),
        ["vega.cfg.maintenance"],
        Syntax: "save");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return InvalidUsage("command.save.usage", "Usage: save.");
        }

        await server.SaveAsync(cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.save.completed")
                .WithDefaultText("World save completed."));
    }

    private static CommandExecutionResult InvalidUsage(string code, string text) => new(
        CommandExecutionStatus.InvalidUsage,
        code,
        [MessageDescriptor.Error(code).WithDefaultText(text)]);
}

public sealed class ShutdownCommand(IServerControl server) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "off",
        ["exit", "stop", "shutdown", "off-nosave", "exit-nosave", "stop-nosave"],
        ["vega.cfg.maintenance"],
        Syntax: "off [reason] | off-nosave [reason]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        bool saveWorld = !invocation.Name.EndsWith("-nosave", StringComparison.OrdinalIgnoreCase);
        string reason = invocation.Arguments.Count == 0
            ? saveWorld
                ? "Server shutting down."
                : "Server shutting down without saving."
            : string.Join(' ', invocation.Arguments);

        if (saveWorld)
        {
            await server.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await server.ShutdownWithoutSaveAsync(reason, cancellationToken).ConfigureAwait(false);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Warning(
                    saveWorld ? "command.shutdown.started" : "command.shutdown_nosave.started",
                    reason)
                .WithDefaultText(
                    saveWorld
                        ? "Server shutdown started: {0}"
                        : "Server shutdown without save started: {0}"));
    }
}

public sealed class ServerPasswordCommand(IServerControl server) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "serverpassword",
        Array.Empty<string>(),
        ["vega.cfg.password"],
        Syntax: "serverpassword <new-password>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 1)
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.invalid_usage",
                [MessageDescriptor.Error("command.serverpassword.usage")
                    .WithDefaultText("Usage: serverpassword <new-password>.")]);
        }

        await server.SetPasswordAsync(invocation.Arguments[0], cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.serverpassword.changed")
                .WithDefaultText(string.IsNullOrEmpty(invocation.Arguments[0])
                    ? "Server password cleared."
                    : "Server password changed."));
    }
}
