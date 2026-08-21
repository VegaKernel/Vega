using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands;

public sealed class HelpCommand(ICommandCatalog commands) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "help",
        ["commands"],
        Array.Empty<string>());

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
                    .WithDefaultText("Available commands: {0}. Use /help <command> for aliases and permissions.")));
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
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.help.command", match.Name, aliases, permissions)
                .WithDefaultText("/{0} | aliases: {1} | permissions: {2}")));
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
        Array.Empty<string>());

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.aliases.usage",
                [MessageDescriptor.Error("command.aliases.usage")
                    .WithDefaultText("Usage: aliases.")]));
        }

        string[] aliases = commands.SnapshotCommands()
            .Where(command => command.IsAvailableTo(invocation.Principal))
            .SelectMany(command => command.Aliases.Select(alias => $"{alias} -> {command.Name}"))
            .OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.aliases.list", aliases.Length == 0 ? "none" : string.Join(", ", aliases))
                .WithDefaultText("Aliases: {0}.")));
    }
}
