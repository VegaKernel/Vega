using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands.Players;

public sealed class LanguageCommand(IPlayerCultureService cultures) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "lang",
        ["language"],
        Array.Empty<string>());

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not ICommandCulturePrincipal principal)
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.lang.player-only",
                MessageDescriptor.Error("command.lang.player-only")
                    .WithDefaultText("Language selection is available to players; the console uses the configured server culture.")));
        }

        if (invocation.Arguments.Count == 0)
        {
            PlayerCulture current = cultures.Resolve(principal.CultureSubjectId);
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.lang.current", current.Name)
                    .WithDefaultText("Current language: {0}. Use /lang list, /lang <culture>, or /lang reset.")));
        }

        if (invocation.Arguments.Count != 1)
        {
            return ValueTask.FromResult(Usage());
        }

        string action = invocation.Arguments[0];
        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.lang.list",
                        string.Join(", ", cultures.AvailableCultures.Select(static culture => culture.Name)))
                    .WithDefaultText("Available languages: {0}.")));
        }

        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            cultures.ResetSession(principal.CultureSubjectId);
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.lang.reset", cultures.ServerCulture.Name)
                    .WithDefaultText("Language reset to the server default: {0}.")));
        }

        if (!cultures.TrySetSession(principal.CultureSubjectId, action, out PlayerCulture? selected))
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.lang.unavailable",
                MessageDescriptor.Error(
                        "command.lang.unavailable",
                        action,
                        string.Join(", ", cultures.AvailableCultures.Select(static culture => culture.Name)))
                    .WithDefaultText("Language '{0}' is unavailable. Available languages: {1}.")));
        }

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.lang.changed", selected!.Name)
                .WithDefaultText("Language changed to {0}.")));
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.lang.usage",
        [MessageDescriptor.Error("command.lang.usage")
            .WithDefaultText("Usage: lang | lang list | lang <culture> | lang reset.")]);
}
