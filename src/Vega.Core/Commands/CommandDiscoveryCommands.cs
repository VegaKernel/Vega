using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands;

public sealed class HelpCommand(ICommandCatalog commands) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "help",
        ["commands"],
        Array.Empty<string>(),
        Syntax: "help [command]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count > 1)
        {
            return ValueTask.FromResult(InvalidUsage());
        }

        CommandDescriptor[] visible = commands.SnapshotCommands()
            .Where(command => command.IsAvailableTo(invocation.Principal))
            .ToArray();

        if (invocation.Arguments.Count == 0)
        {
            string names = visible.Length == 0
                ? "none"
                : string.Join(", ", visible.Select(static command => command.Name));
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.help.list", names)
                    .WithDefaultText("Available commands: {0}. Use /help <command> for syntax, aliases and permissions.")));
        }

        string requested = invocation.Arguments[0];
        CommandDescriptor? match = visible.FirstOrDefault(command =>
            command.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            command.Aliases.Any(alias => alias.Equals(requested, StringComparison.OrdinalIgnoreCase)));
        if (match is null)
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.help.not_found",
                MessageDescriptor.Error("command.help.not_found", requested)
                    .WithDefaultText("Unknown or unavailable command: {0}.")));
        }

        string aliases = match.Aliases.Count == 0 ? "none" : string.Join(", ", match.Aliases);
        string permissions = match.Permissions.Count == 0 ? "none" : string.Join(" or ", match.Permissions);
        string syntax = string.IsNullOrWhiteSpace(match.Syntax) ? match.Name : match.Syntax;
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.help.command", match.Name, syntax, aliases, permissions)
                .WithDefaultText("/{0} | syntax: /{1} | aliases: {2} | permissions: {3}")));
    }

    private static CommandExecutionResult InvalidUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.help.usage",
        [MessageDescriptor.Error("command.help.usage")
            .WithDefaultText("Usage: help [command]. Alias: commands.")]);
}

public sealed class AliasesCommand(ICommandCatalog commands) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "aliases",
        Array.Empty<string>(),
        Array.Empty<string>(),
        Syntax: "aliases <command or alias>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return ValueTask.FromResult(InvalidUsage());
        }

        string requested = string.Join(" ", invocation.Arguments).Trim();
        if (requested.StartsWith("/", StringComparison.Ordinal))
        {
            requested = requested[1..];
        }

        if (requested.Length == 0)
        {
            return ValueTask.FromResult(InvalidUsage());
        }

        CommandDescriptor[] matches = commands.SnapshotCommands()
            .Where(command =>
                command.Name.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
                command.Aliases.Any(alias => alias.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (matches.Length == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.aliases.not_found",
                MessageDescriptor.Error("command.aliases.not_found", requested)
                    .WithDefaultText("Unknown command or alias: {0}.")));
        }

        MessageDescriptor[] messages = matches.Select(command =>
        {
            string aliases = command.Aliases.Count == 0
                ? "none"
                : string.Join(", ", command.Aliases.Select(static alias => $"/{alias}"));
            return MessageDescriptor.Info("command.aliases.command", command.Name, aliases)
                .WithDefaultText("Aliases of /{0}: {1}.");
        }).ToArray();
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(messages));
    }

    private static CommandExecutionResult InvalidUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.aliases.usage",
        [MessageDescriptor.Error("command.aliases.usage")
            .WithDefaultText("Usage: aliases <command or alias>.")]);
}
