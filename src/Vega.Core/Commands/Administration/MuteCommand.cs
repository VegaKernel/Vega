using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Moderation;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class MuteCommand(
    IPlayerDirectory players,
    IPlayerAccountSessionService accountSessions,
    IMuteService mutes,
    TimeProvider? timeProvider = null) : ICommandHandler
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "mute",
        ["unmute"],
        ["tshock.admin.mute"],
        Syntax: "mute <online-player> <duration> [reason] | mute add <name|account> <value> <duration> [reason] | mute list | mute show <id> | unmute <id>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Name.Equals("unmute", StringComparison.OrdinalIgnoreCase))
        {
            return await RevokeAsync(invocation.Arguments, invocation.Principal, cancellationToken).ConfigureAwait(false);
        }

        if (invocation.Arguments.Count == 0) return Usage();
        string action = invocation.Arguments[0].ToLowerInvariant();
        if (action == "list") return List();
        if (action == "show") return Show(invocation.Arguments);
        if (action is "remove" or "revoke")
        {
            return await RevokeAsync(invocation.Arguments.Skip(1).ToArray(), invocation.Principal, cancellationToken)
                .ConfigureAwait(false);
        }

        if (action == "add")
        {
            return await AddTypedAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        return await AddOnlineAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> AddOnlineAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 2 || !TryParseDuration(invocation.Arguments[1], out TimeSpan duration))
        {
            return Usage();
        }

        PlayerSnapshot[] matches = FindPlayers(invocation.Arguments[0]);
        if (matches.Length != 1)
        {
            return Failure(
                matches.Length == 0 ? "command.mute.player-not-found" : "command.mute.multiple-matches",
                matches.Length == 0 ? "Player not found." : "Multiple players matched: {0}.",
                string.Join(", ", matches.Select(static player => player.Name)));
        }

        PlayerSnapshot target = matches[0];
        accountSessions.TryGetAccount(target.Id, out AccountSnapshot? account);
        return await CreateAsync(
            account is null ? MuteSubjectKind.PlayerName : MuteSubjectKind.Account,
            account?.Name ?? target.Name,
            duration,
            invocation.Arguments.Count > 2 ? string.Join(" ", invocation.Arguments.Skip(2)) : "Muted by an operator.",
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> AddTypedAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 4 ||
            !TryParseKind(invocation.Arguments[1], out MuteSubjectKind kind) ||
            !TryParseDuration(invocation.Arguments[3], out TimeSpan duration))
        {
            return Usage();
        }

        return await CreateAsync(
            kind,
            invocation.Arguments[2],
            duration,
            invocation.Arguments.Count > 4 ? string.Join(" ", invocation.Arguments.Skip(4)) : "Muted by an operator.",
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> CreateAsync(
        MuteSubjectKind kind,
        string subject,
        TimeSpan duration,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();
        var mute = new MuteSnapshot(
            MuteId.New(), kind, subject.Trim(), reason.Trim(), actor, now, now.Add(duration), 1);
        MuteMutationResult result;
        try
        {
            result = await mutes.CreateAsync(mute, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Failure("command.mute.invalid", ex.Message);
        }

        return result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.mute.applied", mute.Id.ToString(), $"{kind}:{mute.SubjectValue}", mute.ExpiresAt, mute.Reason)
                    .WithDefaultText("Mute {0} created for {1}; expires {2:u}; reason: {3}."))
            : Failure("command.mute.persist-failed", "Unable to persist mute: {0}.", result.Code);
    }

    private CommandExecutionResult List()
    {
        MuteSnapshot[] active = mutes.SnapshotActive(time.GetUtcNow()).Take(100).ToArray();
        return active.Length == 0
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.mute.list-empty").WithDefaultText("There are no active mutes."))
            : CommandExecutionResult.WithMessages(active.Select(Format).ToArray());
    }

    private CommandExecutionResult Show(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2 || !Guid.TryParse(arguments[1], out Guid id)) return Usage();
        MuteSnapshot? mute = mutes.SnapshotActive(time.GetUtcNow())
            .FirstOrDefault(candidate => candidate.Id.Value == id);
        return mute is null ? Failure("mute.not-found", "Active mute not found.") :
            CommandExecutionResult.WithMessages(Format(mute));
    }

    private async ValueTask<CommandExecutionResult> RevokeAsync(
        IReadOnlyList<string> arguments,
        ICommandPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 1 || !Guid.TryParse(arguments[0], out Guid id)) return Usage();
        MuteSnapshot? mute = mutes.SnapshotActive(time.GetUtcNow())
            .FirstOrDefault(candidate => candidate.Id.Value == id);
        if (mute is null) return Failure("mute.not-found", "Active mute not found.");
        MuteMutationResult result = await mutes.RevokeAsync(
            mute.Id, mute.Revision, principal.Name, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.unmute.applied", mute.Id.ToString())
                    .WithDefaultText("Mute {0} was revoked."))
            : Failure("command.unmute.failed", "Unable to revoke mute: {0}.", result.Code);
    }

    private static MessageDescriptor Format(MuteSnapshot mute) =>
        MessageDescriptor.Info(
                "command.mute.list-entry",
                mute.Id.ToString(),
                $"{mute.SubjectKind}:{mute.SubjectValue}",
                mute.ExpiresAt,
                mute.IssuedBy,
                mute.Reason,
                mute.Revision)
            .WithDefaultText("{0} | {1} | expires {2:u} | by {3} | {4} | revision {5}");

    private PlayerSnapshot[] FindPlayers(string search)
    {
        PlayerSnapshot[] online = players.SnapshotOnlinePlayers().ToArray();
        PlayerSnapshot[] exact = online
            .Where(player => player.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length > 0
            ? exact
            : online.Where(player => player.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool TryParseKind(string value, out MuteSubjectKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "name" or "player" => MuteSubjectKind.PlayerName,
            "account" => MuteSubjectKind.Account,
            _ => (MuteSubjectKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length < 2 ||
            !double.TryParse(normalized[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount) ||
            amount <= 0)
        {
            return false;
        }

        double minutes = normalized[^1] switch
        {
            'm' => amount,
            'h' => amount * 60,
            'd' => amount * 24 * 60,
            'w' => amount * 7 * 24 * 60,
            _ => double.NaN
        };
        if (!double.IsFinite(minutes) || minutes > TimeSpan.FromDays(365).TotalMinutes) return false;
        duration = TimeSpan.FromMinutes(minutes);
        return true;
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.mute.usage",
        [MessageDescriptor.Error("command.mute.usage")
            .WithDefaultText("Usage: mute <online-player> <30m|12h|7d> [reason] | mute add <name|account> <value> <duration> [reason] | mute list | mute show <id> | unmute <id>.")]);

    private static CommandExecutionResult Failure(string code, string text, params object?[] arguments) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, arguments).WithDefaultText(text));
}
