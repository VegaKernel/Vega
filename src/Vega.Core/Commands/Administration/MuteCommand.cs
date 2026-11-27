using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Moderation;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class MuteCommand(
    IPlayerDirectory players,
    IPlayerAccountSessionService accountSessions,
    IMuteService mutes,
    TimeProvider? timeProvider = null,
    IAccountService? accounts = null,
    IAuthorizationService? groups = null,
    IPlayerAuthorizationService? playerAuthorization = null,
    IRoleLeaseService? roleLeases = null) : ICommandHandler
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "mute",
        ["unmute"],
        ["vega.admin.mute", "extendedbans.mute"],
        Syntax: "mute <online-player> <duration> [reason] | mute add <player/account> [time] [reason] | mute del <player/account> | mute add <name|account> <value> <duration> [reason] | mute list [page] | mute show <id> | unmute <id>");

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
        if (action == "list") return List(invocation.Arguments);
        if (action is "del" or "delete") return await DeleteByPlayerAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (action == "show") return Show(invocation.Arguments);
        if (action is "remove" or "revoke")
        {
            return await RevokeAsync(invocation.Arguments.Skip(1).ToArray(), invocation.Principal, cancellationToken)
                .ConfigureAwait(false);
        }

        if (action == "add") return await AddCompatibilityAsync(invocation, cancellationToken).ConfigureAwait(false);
        return await AddOnlineAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> DeleteByPlayerAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2) return Usage();
        string name = invocation.Arguments[1];
        var matches = FindPlayers(name, invocation.WorldId);
        if (matches.Length > 1) return Failure("command.mute.multiple-matches", "Multiple players matched.");
        var online = matches.FirstOrDefault();
        if (online is not null && accountSessions.TryGetAccount(online.Id, out var onlineAccount) && onlineAccount is not null) name = onlineAccount.Name;
        else if (online is not null) name = online.Name;
        AccountSnapshot? account = accounts is null ? null : await accounts.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var (ip, uuid) = ObservedIdentifiers(account, online);
        var snapshot = mutes.SnapshotActive(time.GetUtcNow());
        var active = snapshot.Where(m => m.SubjectValue.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0 && account is null)
            active = snapshot.Where(m => m.SubjectKind == MuteSubjectKind.PlayerName &&
                ((m.IpAddress is not null && m.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase)) || (m.Uuid is not null && m.Uuid.Equals(uuid, StringComparison.Ordinal)))).ToArray();
        if (active.Length == 0) return Failure("mute.not-found", "The player is not muted.");
        foreach (var mute in active)
        {
            var result = await RevokeAsync([mute.Id.Value.ToString()], invocation.Principal, cancellationToken).ConfigureAwait(false);
            if (result.Status != CommandExecutionStatus.Success) return result;
        }
        return CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.mute.deleted", invocation.Principal.Name, name).WithDefaultText("{0} unmuted {1}."));
    }

    private async ValueTask<CommandExecutionResult> AddCompatibilityAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count >= 4 && TryParseKind(invocation.Arguments[1], out _))
            return await AddTypedAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (invocation.Arguments.Count < 2) return Usage();
        string name = invocation.Arguments[1];
        string expiry = invocation.Arguments.Count > 2 ? invocation.Arguments[2] : "0";
        TimeSpan? duration = null;
        if (expiry != "0" && !expiry.Equals("permanent", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDuration(expiry, out var finite)) return Usage();
            duration = finite;
        }
        var matches = FindPlayers(name, invocation.WorldId);
        if (matches.Length > 1) return Failure("command.mute.multiple-matches", "Multiple players matched.");
        AccountSnapshot? account = null;
        if (matches.Length == 1) accountSessions.TryGetAccount(matches[0].Id, out account);
        else if (accounts is not null) account = await accounts.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);
        if (matches.Length == 0 && account is null) return Failure("command.mute.player-not-found", "Invalid user.");
        if (!CanMute(invocation.Principal, matches.FirstOrDefault(), account)) return Failure("command.mute.authority", "You can't mute {0}!", name);
        var (ip, uuid) = ObservedIdentifiers(account, matches.FirstOrDefault());
        return await CreateAsync(account is null ? MuteSubjectKind.PlayerName : MuteSubjectKind.Account,
            account?.Name ?? matches[0].Name, duration, invocation.Arguments.Count > 3 ? string.Join(" ", invocation.Arguments.Skip(3)) : "Offensive behavior", invocation.Principal.Name, cancellationToken, ip, uuid).ConfigureAwait(false);
    }

    private static (string? Ip, string? Uuid) ObservedIdentifiers(AccountSnapshot? account, PlayerSnapshot? online)
    {
        string? ip = account?.LastKnownIp, uuid = account?.LastKnownUuid;
        if (online is not null)
        {
            if (BanCommand.TryGetIpAddress(online.Connection?.EffectiveEndpoint ?? online.RemoteEndpoint, out var liveIp)) ip = liveIp;
            uuid = online.Connection?.ClientUuid ?? uuid;
        }
        return (ip, uuid);
    }

    private async ValueTask<CommandExecutionResult> AddOnlineAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 2 || !TryParseDuration(invocation.Arguments[1], out TimeSpan duration))
        {
            return Usage();
        }

        PlayerSnapshot[] matches = FindPlayers(invocation.Arguments[0], invocation.WorldId);
        if (matches.Length != 1)
        {
            return Failure(
                matches.Length == 0 ? "command.mute.player-not-found" : "command.mute.multiple-matches",
                matches.Length == 0 ? "Player not found." : "Multiple players matched: {0}.",
                string.Join(", ", matches.Select(static player => player.Name)));
        }

        PlayerSnapshot target = matches[0];
        accountSessions.TryGetAccount(target.Id, out AccountSnapshot? account);
        if (!CanMute(invocation.Principal, target, account)) return Failure("command.mute.authority", "You can't mute {0}!", target.Name);
        BanCommand.TryGetIpAddress(target.Connection?.EffectiveEndpoint ?? target.RemoteEndpoint, out var ip);
        return await CreateAsync(
            account is null ? MuteSubjectKind.PlayerName : MuteSubjectKind.Account,
            account?.Name ?? target.Name,
            duration,
            invocation.Arguments.Count > 2 ? string.Join(" ", invocation.Arguments.Skip(2)) : "Muted by an operator.",
            invocation.Principal.Name,
            cancellationToken, ip, target.Connection?.ClientUuid).ConfigureAwait(false);
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

        string subject = invocation.Arguments[2];
        AccountSnapshot? account = kind == MuteSubjectKind.Account && accounts is not null ? await accounts.FindByNameAsync(subject, cancellationToken).ConfigureAwait(false) : null;
        var online = players.SnapshotOnlinePlayers().Where(p => kind == MuteSubjectKind.PlayerName
            ? p.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)
            : accountSessions.TryGetAccount(p.Id, out var bound) && bound?.Name.Equals(subject, StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (online.Any(p => !CanMute(invocation.Principal, p, account)) || !CanMute(invocation.Principal, null, account)) return Failure("command.mute.authority", "You can't mute {0}!", subject);

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
        TimeSpan? duration,
        string reason,
        string actor,
        CancellationToken cancellationToken, string? ip = null, string? uuid = null)
    {
        DateTimeOffset now = time.GetUtcNow();
        var mute = new MuteSnapshot(
            MuteId.New(), kind, subject.Trim(), reason.Trim(), actor, now, duration is { } finite ? now.Add(finite) : DateTimeOffset.MaxValue, 1)
        { IpAddress = ip, Uuid = uuid is { Length: > 0 and <= 255 } && !uuid.Any(char.IsControl) ? uuid : null };
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

    private CommandExecutionResult List(IReadOnlyList<string> arguments)
    {
        int page = 1;
        if (arguments.Count > 2 || (arguments.Count == 2 && (!int.TryParse(arguments[1], out page) || page < 1 || page > int.MaxValue / 10))) return Usage();
        MuteSnapshot[] active = mutes.SnapshotActive(time.GetUtcNow()).Skip((page - 1) * 10).Take(arguments.Count == 2 ? 10 : 100).ToArray();
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

    private PlayerSnapshot[] FindPlayers(string search, Vega.Contracts.Worlds.WorldId world)
    {
        PlayerSnapshot[] online = players.SnapshotOnlinePlayers().Where(p => p.WorldId == world).ToArray();
        PlayerSnapshot[] exact = online
            .Where(player => player.Name.Equals(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length > 0 ? exact : PlayerTargetLookup.FindByNameOrSlot(players, world, search).ToArray();
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

    private bool CanMute(ICommandPrincipal principal, PlayerSnapshot? player, AccountSnapshot? account)
    {
        if (principal.IsServer || groups is null) return true;
        const string root = "extendedbans.mute";
        string? group = account is null ? null : roleLeases?.FindActive(account.Id, time.GetUtcNow())?.GroupName ?? account.GroupName;
        bool TargetHas(string permission) => player is not null && playerAuthorization is not null
            ? playerAuthorization.HasPermission(player.Id, permission)
            : group is not null && groups.HasPermission(group, permission);
        var known = groups.SnapshotGroups().SelectMany(g => g.Permissions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int Level(Func<string, bool> has) => DynamicPermissionLimit.Resolve(root, known, has, has("vega.admin.mute") ? 1 : 0);
        return Level(principal.HasPermission) >= Level(TargetHas);
    }

    private static bool TryParseDuration(string value, out TimeSpan duration) =>
        CommandDuration.TryParse(value, TimeSpan.FromDays(365), out duration);

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.mute.usage",
        [MessageDescriptor.Error("command.mute.usage")
            .WithDefaultText("Usage: mute <online-player> <30m|12h|7d> [reason] | mute add <name|account> <value> <duration> [reason] | mute list | mute show <id> | unmute <id>.")]);

    private static CommandExecutionResult Failure(string code, string text, params object?[] arguments) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, arguments).WithDefaultText(text));
}
