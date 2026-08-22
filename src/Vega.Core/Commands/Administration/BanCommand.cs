using System.Net;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class BanCommand(
    IPlayerDirectory players,
    IPlayerOperations playerOperations,
    IBanService bans,
    TimeProvider? timeProvider = null) : ICommandHandler
{
    private const string BanPermission = "tshock.admin.ban";
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "ban",
        Array.Empty<string>(),
        [BanPermission]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 1 || string.IsNullOrWhiteSpace(invocation.Arguments[0]))
        {
            return InvalidUsage("command.ban.usage", "Usage: ban <player> [reason].");
        }

        IReadOnlyList<PlayerSnapshot> matches = FindPlayers(invocation.Arguments[0]);
        if (matches.Count == 0)
        {
            return Failure("command.ban.player-not-found", "Player not found. Unable to ban the player.");
        }

        if (matches.Count > 1)
        {
            return Failure(
                "command.ban.multiple-matches",
                "Multiple players matched: {0}.",
                string.Join(", ", matches.Select(static player => player.Name)));
        }

        PlayerSnapshot target = matches[0];
        string reason = invocation.Arguments.Count > 1
            ? string.Join(" ", invocation.Arguments.Skip(1))
            : "Misbehaviour.";
        DateTimeOffset now = time.GetUtcNow();
        var subjects = new List<GlobalBanSubject>
        {
            new(GlobalBanSubjectKind.PlayerName, target.Name)
        };
        if (TryGetIpAddress(target.RemoteEndpoint, out string? ipAddress) && ipAddress is not null)
        {
            subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, ipAddress));
        }

        foreach (GlobalBanSubject subject in subjects)
        {
            bool applied = await bans.CreateAsync(
                new GlobalBanCreated(
                    GlobalBanId.New(),
                    subject,
                    reason,
                    invocation.Principal.Name,
                    now,
                    null,
                    1),
                cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                return Failure("command.ban.persist-failed", "Unable to persist the ban for {0}.", target.Name);
            }
        }

        PlayerOperationResult kick = await playerOperations
            .KickAsync(target.Id, $"Banned: {reason}", cancellationToken)
            .ConfigureAwait(false);
        if (!kick.IsSuccess)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.ban.applied", target.Name, reason)
                    .WithDefaultText("Banned {0} for '{1}'."),
                MessageDescriptor.Warning("command.ban.kick-failed", target.Name)
                    .WithDefaultText("The ban was saved, but {0} could not be disconnected immediately."));
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.ban.applied", target.Name, reason)
                .WithDefaultText("Banned {0} for '{1}'."));
    }

    private IReadOnlyList<PlayerSnapshot> FindPlayers(string rawSearch)
    {
        string search = rawSearch.Trim();
        if (search.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(search[4..], out int slot)
                ? players.SnapshotOnlinePlayers().Where(player => player.Id.Slot == slot).ToArray()
                : Array.Empty<PlayerSnapshot>();
        }

        PlayerSnapshot[] exact = players.SnapshotOnlinePlayers()
            .Where(player => string.Equals(player.Name, search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length > 0
            ? exact
            : players.SnapshotOnlinePlayers()
                .Where(player => player.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    internal static bool TryGetIpAddress(string? remoteEndpoint, out string? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            return false;
        }

        if (IPAddress.TryParse(remoteEndpoint, out IPAddress? direct))
        {
            address = direct.ToString();
            return true;
        }

        if (IPEndPoint.TryParse(remoteEndpoint, out IPEndPoint? endpoint))
        {
            address = endpoint.Address.ToString();
            return true;
        }

        return false;
    }

    private static CommandExecutionResult InvalidUsage(string errorCode, string defaultText) =>
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
