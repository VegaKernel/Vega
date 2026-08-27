using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class KickCommand(
    IPlayerDirectory players,
    IPlayerOperations playerOperations,
    IPlayerAuthorizationService authorization) : ICommandHandler
{
    private const string KickPermission = "vega.admin.kick";
    private const string KickImmunityPermission = "vega.admin.nokick";

    public CommandDescriptor Descriptor { get; } = new(
        "kick",
        Array.Empty<string>(),
        [KickPermission],
        Syntax: "kick <player> [reason]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 1 || string.IsNullOrWhiteSpace(invocation.Arguments[0]))
        {
            return InvalidUsage(
                "command.kick.usage",
                "Usage: kick <player> [reason].");
        }

        IReadOnlyList<PlayerSnapshot> matches = OnlinePlayerSearch.Resolve(players, invocation.Arguments[0]);
        if (matches.Count == 0)
        {
            return Failure(
                "command.kick.player-not-found",
                "Player not found. Unable to kick the player.");
        }

        if (matches.Count > 1)
        {
            return Failure(
                "command.kick.multiple-matches",
                "Multiple players matched: {0}.",
                string.Join(", ", matches.Select(static player => player.Name)));
        }

        PlayerSnapshot target = matches[0];
        if (!invocation.Principal.IsServer &&
            authorization.HasPermission(target.Id, KickImmunityPermission))
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.Forbidden,
                "command.kick.immune",
                [MessageDescriptor.Error("command.kick.immune")
                    .WithDefaultText("You cannot kick a player with kick immunity.")]);
        }

        string reason = invocation.Arguments.Count > 1
            ? string.Join(" ", invocation.Arguments.Skip(1))
            : "Misbehaviour.";

        PlayerOperationResult operation = await playerOperations
            .KickAsync(target.Id, $"Kicked: {reason}", cancellationToken)
            .ConfigureAwait(false);

        if (!operation.IsSuccess)
        {
            return Failure(
                operation.ErrorCode ?? "command.kick.failed",
                "Unable to kick {0}.",
                target.Name);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.kick.applied", target.Name, reason)
                .WithDefaultText("Kicked {0} for '{1}'."));
    }

    private static CommandExecutionResult InvalidUsage(
        string errorCode,
        string defaultText) =>
        new(
            CommandExecutionStatus.InvalidUsage,
            errorCode,
            [MessageDescriptor.Error(errorCode).WithDefaultText(defaultText)]);

    private static CommandExecutionResult Failure(
        string errorCode,
        string defaultText,
        params object?[] arguments) =>
        CommandExecutionResult.Failed(
            errorCode,
            MessageDescriptor.Error(errorCode, arguments).WithDefaultText(defaultText));
}
