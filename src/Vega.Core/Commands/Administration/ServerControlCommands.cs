using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Runtime;

namespace Vega.Core.Commands.Administration;

public sealed class SaveCommand(IServerControl server) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "save",
        Array.Empty<string>(),
        ["tshock.cfg.maintenance"],
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
        "shutdown",
        ["stop", "off"],
        ["tshock.cfg.maintenance"],
        Syntax: "shutdown [reason]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        string reason = invocation.Arguments.Count == 0
            ? "Server shutting down."
            : string.Join(' ', invocation.Arguments);
        await server.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Warning("command.shutdown.started", reason)
                .WithDefaultText("Server shutdown started: {0}"));
    }
}
