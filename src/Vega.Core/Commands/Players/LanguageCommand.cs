using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands.Players;

public sealed class LanguageCommand(
    IPlayerCultureService cultures,
    IPlayerAccountSessionService? accountSessions = null) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "lang",
        ["language"],
        Array.Empty<string>(),
        Syntax: "lang | lang list | lang <culture> | lang reset");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not ICommandCulturePrincipal principal)
        {
            return CommandExecutionResult.Failed(
                "command.lang.player-only",
                MessageDescriptor.Error("command.lang.player-only")
                    .WithDefaultText("Language selection is available to players; the console uses the configured server culture."));
        }

        if (invocation.Arguments.Count == 0)
        {
            PlayerCulture current = cultures.Resolve(principal.CultureSubjectId);
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.lang.current", current.Name)
                    .WithDefaultText("Current language: {0}. Use /lang list, /lang <culture>, or /lang reset."));
        }

        if (invocation.Arguments.Count != 1)
        {
            return Usage();
        }

        string action = invocation.Arguments[0];
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.lang.list",
                        string.Join(", ", cultures.AvailableCultures.Select(static culture => culture.Name)))
                    .WithDefaultText("Available languages: {0}."));
        }

        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (principal is IPlayerCommandPrincipal player &&
                player.AccountName is not null &&
                accountSessions is not null &&
                !await accountSessions.SetCultureAsync(player.PlayerId, null, cancellationToken).ConfigureAwait(false))
            {
                return PersistenceFailure();
            }

            cultures.ResetSession(principal.CultureSubjectId);
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.lang.reset", cultures.ServerCulture.Name)
                    .WithDefaultText("Language reset to the server default: {0}."));
        }

        if (!cultures.TrySetSession(principal.CultureSubjectId, action, out PlayerCulture? selected))
        {
            return CommandExecutionResult.Failed(
                "command.lang.unavailable",
                MessageDescriptor.Error(
                        "command.lang.unavailable",
                        action,
                        string.Join(", ", cultures.AvailableCultures.Select(static culture => culture.Name)))
                    .WithDefaultText("Language '{0}' is unavailable. Available languages: {1}."));
        }

        if (principal is IPlayerCommandPrincipal authenticated &&
            authenticated.AccountName is not null &&
            accountSessions is not null &&
            !await accountSessions.SetCultureAsync(
                authenticated.PlayerId,
                selected!.Name,
                cancellationToken).ConfigureAwait(false))
        {
            cultures.ResetSession(principal.CultureSubjectId);
            return PersistenceFailure();
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.lang.changed", selected!.Name)
                .WithDefaultText("Language changed to {0}."));
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.lang.usage",
        [MessageDescriptor.Error("command.lang.usage")
            .WithDefaultText("Usage: lang | lang list | lang <culture> | lang reset.")]);

    private static CommandExecutionResult PersistenceFailure() => CommandExecutionResult.Failed(
        "command.lang.persistence-failed",
        MessageDescriptor.Error("command.lang.persistence-failed")
            .WithDefaultText("The account language could not be saved; please try again."));
}
