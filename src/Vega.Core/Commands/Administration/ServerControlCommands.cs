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

public sealed class ShutdownCommand(IServerControl server, bool save = true) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = save
        ? new("shutdown", ["stop", "off", "exit"], ["vega.cfg.maintenance"], Syntax: "shutdown [reason]")
        : new("off-nosave", ["exit-nosave", "stop-nosave"], ["vega.cfg.maintenance.nosave"], Syntax: "off-nosave [reason]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        string reason = invocation.Arguments.Count == 0
            ? "Server shutting down."
            : string.Join(' ', invocation.Arguments);
        if (save) await server.ShutdownAsync(reason, cancellationToken).ConfigureAwait(false);
        else await server.ShutdownWithoutSaveAsync(reason, cancellationToken).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Warning(save ? "command.shutdown.started" : "command.shutdown.nosave", reason)
                .WithDefaultText(save ? "Server shutdown started: {0}" : "Server shutdown without world save started: {0}"));
    }
}

public sealed class ServerPasswordCommand(IServerControl server) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("serverpassword", [], ["vega.cfg.password"], Syntax: "serverpassword <password> | serverpassword clear | serverpassword set <password>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        string? password = args.Count switch
        {
            1 => args[0].Equals("clear", StringComparison.OrdinalIgnoreCase) ? string.Empty : args[0],
            2 when args[0].Equals("set", StringComparison.OrdinalIgnoreCase) => args[1],
            _ => null
        };
        if (password is null || password.Length > 128 || password.Any(char.IsControl))
            return new(CommandExecutionStatus.InvalidUsage, "command.serverpassword.usage");
        await server.SetPasswordAsync(password, token).ConfigureAwait(false);
        return CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.serverpassword.changed")
            .WithDefaultText(password.Length == 0 ? "Server password cleared for new connections." : "Server password changed for new connections."));
    }
}
