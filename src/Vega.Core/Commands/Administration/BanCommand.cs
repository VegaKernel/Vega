using System.Net;
using Vega.Contracts.Accounts;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Core.Commands.Administration;

public sealed class BanCommand(
    IPlayerDirectory players,
    IPlayerOperations playerOperations,
    IBanService bans,
    TimeProvider? timeProvider = null,
    IPlayerAccountSessionService? accountSessions = null,
    IAccountService? accounts = null) : ICommandHandler
{
    private const string BanPermission = "vega.admin.ban";
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "ban",
        ["unban", "bantools", "bantool", "bt"],
        [BanPermission, "tshock.admin.ban"],
        Syntax: "ban <player> [reason] | ban add <name|account|uuid|ip|cidr> <value> <duration|permanent> [reason] | ban list | ban show|details <id> | ban del <id> | unban <id> | ban help|identifiers|examples | bt help|add|addip|addrange|delrange|listrange|info");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Name.Equals("bt", StringComparison.OrdinalIgnoreCase) || invocation.Name.StartsWith("bantool", StringComparison.OrdinalIgnoreCase))
            return await ExecuteBanToolsAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (string.Equals(invocation.Name, "unban", StringComparison.OrdinalIgnoreCase))
        {
            return await RevokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        if (invocation.Arguments.Count < 1 || string.IsNullOrWhiteSpace(invocation.Arguments[0]))
        {
            return InvalidUsage(
                "command.ban.usage",
                "Usage: ban <player> [reason] | ban add <name|account|uuid|ip|cidr> <value> <duration|permanent> [reason] | ban list | ban show <id> | unban <id>.");
        }

        string action = invocation.Arguments[0].ToLowerInvariant();
        if (action == "add")
        {
            return await AddTypedAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        if (action == "list")
        {
            return List(invocation);
        }

        if (action is "show" or "details")
        {
            return Show(invocation);
        }

        if (action is "revoke" or "remove" or "del")
        {
            return await RevokeAsync(invocation with
            {
                Arguments = invocation.Arguments.Skip(1).ToArray()
            }, cancellationToken).ConfigureAwait(false);
        }

        if (invocation.Arguments.Count == 1 && (action is "help" or "identifiers" or "examples"))
        {
            string text = action switch
            {
                "identifiers" => "Ban subject types: name, account, uuid, ip, cidr.",
                "examples" => "ban add name ExamplePlayer 2h reason | ban list | ban del <id>",
                _ => Descriptor.Syntax
            };
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.ban.help", text).WithDefaultText("{0}"));
        }

        IReadOnlyList<PlayerSnapshot> matches = FindPlayers(invocation.Arguments[0], invocation.WorldId);
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
        if (TryGetIpAddress(target.Connection?.EffectiveEndpoint ?? target.RemoteEndpoint, out string? ipAddress) && ipAddress is not null)
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
            .KickAsync(target, $"Banned: {reason}", cancellationToken)
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

    private async ValueTask<CommandExecutionResult> ExecuteBanToolsAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        string action = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        DateTimeOffset now = time.GetUtcNow();
        if (action == "help")
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.bt.help").WithDefaultText("BT Sub-Commands: add <player> <reason> [time] | addip <ip> <reason> [time] | addrange <ip/prefix> <reason> <time> | delrange <ip/prefix> | listrange [page] | info <player/IP>"));
        if (action == "listrange") return ListRanges(invocation, now);
        if (action is "addrange" or "delrange") return await MutateRangeAsync(invocation, action, now, token).ConfigureAwait(false);
        if (action == "addip" && args.Count is 3 or 4)
            return await AddTypedAsync(invocation with { Arguments = ["add", "ip", args[1], args.Count == 4 ? args[3] : "permanent", args[2]] }, token).ConfigureAwait(false);
        if (action is "add" or "info" && args.Count >= 2)
            return await ExecutePlayerBanToolsAsync(invocation, action, now, token).ConfigureAwait(false);
        return BanToolsUsage();
    }

    private CommandExecutionResult ListRanges(CommandInvocation invocation, DateTimeOffset now)
    {
        var args = invocation.Arguments;
        int page = 1;
        if (args.Count > 2 || (args.Count == 2 && (!int.TryParse(args[1], out page) || page < 1 || page > int.MaxValue / 10))) return InvalidUsage("command.bt.usage", "Usage: bt listrange [page number].");
        var ranges = bans.SnapshotActive(now).Where(b => b.Subject.Kind == GlobalBanSubjectKind.Network).Skip((page - 1) * 10).Take(10).ToArray();
        return ranges.Length == 0 ? CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.bt.empty").WithDefaultText("No banned IP ranges.")) : CommandExecutionResult.WithMessages(ranges.Select(b => MessageDescriptor.Info("command.bt.range", FormatSubject(b.Subject, invocation.Principal), b.Reason, FormatExpiry(b.ExpiresAt), b.CreatedBy).WithDefaultText("{0} - Reason: {1}, Expiration: {2}, Banned by {3}")).ToArray());
    }

    private async ValueTask<CommandExecutionResult> MutateRangeAsync(CommandInvocation invocation, string action, DateTimeOffset now, CancellationToken token)
    {
        var args = invocation.Arguments;
        if (!invocation.Principal.IsServer && !invocation.Principal.HasPermission("vega.admin.ban.iprange") && !invocation.Principal.HasPermission("tshock.admin.ban.iprange"))
            return new(CommandExecutionStatus.Forbidden, "command.bt.range.permission");
        if (action == "addrange" && args.Count == 4)
            return await AddTypedAsync(invocation with { Arguments = ["add", "cidr", args[1], args[3], args[2]] }, token).ConfigureAwait(false);
        if (action == "delrange" && args.Count == 2 && bans.TryNormalizeSubject(new(GlobalBanSubjectKind.Network, args[1]), out var normalized, out _))
        {
            var matches = bans.SnapshotActive(now).Where(b => b.Subject == normalized).ToArray();
            if (matches.Length == 0) return Failure("command.ban.not-found", "Active ban not found.");
            foreach (var ban in matches)
                if (!await bans.RevokeAsync(new(ban.Id, invocation.Principal.Name, now, checked(ban.Revision + 1)), token).ConfigureAwait(false))
                    return Failure("command.ban.persist-failed", "Unable to revoke range ban.");
            return CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.bt.removed").WithDefaultText("IP range has been removed."));
        }
        return BanToolsUsage();
    }

    private async ValueTask<CommandExecutionResult> ExecutePlayerBanToolsAsync(CommandInvocation invocation, string action, DateTimeOffset now, CancellationToken token)
    {
        var args = invocation.Arguments;
        var targets = FindPlayers(args[1], invocation.WorldId);
        if (targets.Count > 1) return Failure("command.ban.multiple-matches", "Multiple players matched: {0}.", string.Join(", ", targets.Select(p => p.Name)));
        var subjects = await ResolveBanToolSubjectsAsync(targets, args[1], action, token).ConfigureAwait(false);
        if (subjects.Count == 0) return Failure("command.ban.player-not-found", "Invalid user.");
        if (action == "info" && args.Count == 2) return DescribeBanToolSubjects(subjects, invocation, now);
        if (action == "add" && args.Count is 3 or 4 && TryParseDuration(args.Count == 4 ? args[3] : "permanent", out var duration))
        {
            var tickets = new List<string>();
            foreach (var subject in subjects)
            {
                if (!bans.TryNormalizeSubject(subject, out var normalized, out _) || normalized is null) continue;
                var id = GlobalBanId.New();
                if (await bans.CreateAsync(new(id, normalized, args[2], invocation.Principal.Name, now, duration is { } d ? now.Add(d) : null, 1), token).ConfigureAwait(false)) tickets.Add($"{normalized.Kind}: {id.Value}");
            }
            if (tickets.Count == 0) return Failure("command.ban.persist-failed", "Unable to persist ban.");
            if (targets.Count == 1) await playerOperations.KickAsync(targets[0], $"Banned: {args[2]}", token).ConfigureAwait(false);
            return CommandExecutionResult.WithMessages(MessageDescriptor.Success("command.bt.add", args[1], string.Join(", ", tickets)).WithDefaultText("Player {0} is banned by ticket's: {1}"));
        }
        return BanToolsUsage();
    }

    private async ValueTask<List<GlobalBanSubject>> ResolveBanToolSubjectsAsync(IReadOnlyList<PlayerSnapshot> targets, string input, string action, CancellationToken token)
    {
        var subjects = new List<GlobalBanSubject>();
        if (targets.Count == 1)
        {
            var player = targets[0];
            subjects.Add(new(GlobalBanSubjectKind.PlayerName, player.Name));
            if (TryGetIpAddress(player.Connection?.EffectiveEndpoint ?? player.RemoteEndpoint, out var ip) && ip is not null) subjects.Add(new(GlobalBanSubjectKind.IpAddress, ip));
            if (!string.IsNullOrWhiteSpace(player.Connection?.ClientUuid)) subjects.Add(new(GlobalBanSubjectKind.Uuid, player.Connection.ClientUuid));
            if (accountSessions?.TryGetAccount(player.Id, out var account) == true && account is not null) subjects.Add(new(GlobalBanSubjectKind.Account, account.Name));
        }
        else if (accounts is not null && await accounts.FindByNameAsync(input, token).ConfigureAwait(false) is { } account)
        {
            subjects.Add(new(GlobalBanSubjectKind.Account, account.Name));
            if (account.LastKnownIp is { } lastIp) subjects.Add(new(GlobalBanSubjectKind.IpAddress, lastIp));
            if (account.LastKnownUuid is { } lastUuid) subjects.Add(new(GlobalBanSubjectKind.Uuid, lastUuid));
        }
        else if (action == "info" && IPAddress.TryParse(input, out var address)) subjects.Add(new(GlobalBanSubjectKind.IpAddress, address.ToString()));
        else if (action == "info") subjects.Add(new(GlobalBanSubjectKind.PlayerName, input));
        return subjects;
    }

    private CommandExecutionResult DescribeBanToolSubjects(List<GlobalBanSubject> subjects, CommandInvocation invocation, DateTimeOffset now)
    {
        var matching = bans.SnapshotActive(now).Where(b => subjects.Any(s => bans.TryNormalizeSubject(s, out var n, out _) && n == b.Subject)).ToArray();
        return matching.Length == 0 ? Failure("command.ban.not-found", "Invalid ban") : CommandExecutionResult.WithMessages(matching.Select(b => MessageDescriptor.Info("command.bt.ticket", b.Id.Value, FormatSubject(b.Subject, invocation.Principal), b.Reason).WithDefaultText("Ban ticket: {0} | {1} | {2}")).ToArray());
    }

    private static CommandExecutionResult BanToolsUsage() => InvalidUsage("command.bt.usage", "Invalid syntax. Use /bt help.");

    private async ValueTask<CommandExecutionResult> AddTypedAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 4 ||
            !TryParseKind(invocation.Arguments[1], out GlobalBanSubjectKind kind) ||
            !TryParseDuration(invocation.Arguments[3], out TimeSpan? duration))
        {
            return InvalidUsage(
                "command.ban.add-usage",
                "Usage: ban add <name|account|uuid|ip|cidr> <value> <duration|permanent> [reason]. Duration examples: 30m, 12h, 7d.");
        }

        string reason = invocation.Arguments.Count > 4
            ? string.Join(" ", invocation.Arguments.Skip(4))
            : "No reason supplied.";
        DateTimeOffset now = time.GetUtcNow();
        var created = new GlobalBanCreated(
            GlobalBanId.New(),
            new GlobalBanSubject(kind, invocation.Arguments[2]),
            reason,
            invocation.Principal.Name,
            now,
            duration is TimeSpan finite ? now.Add(finite) : null,
            1);

        try
        {
            if (!await bans.CreateAsync(created, cancellationToken).ConfigureAwait(false))
            {
                return Failure("command.ban.persist-failed", "Unable to persist the ban.");
            }
        }
        catch (ArgumentException ex)
        {
            return Failure("command.ban.invalid-subject", ex.Message);
        }

        BanSnapshot saved = bans.SnapshotActive(now)
            .FirstOrDefault(candidate => candidate.Id == created.BanId) ??
            new BanSnapshot(
                created.BanId,
                created.Subject,
                reason,
                created.CreatedBy,
                now,
                created.ExpiresAt,
                created.Revision);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Success(
                    "command.ban.typed-applied",
                    saved.Id.Value.ToString("D"),
                    FormatSubject(saved.Subject, invocation.Principal),
                    FormatExpiry(saved.ExpiresAt))
                .WithDefaultText("Ban {0} created for {1}; expires {2}."));
    }

    private CommandExecutionResult List(CommandInvocation invocation)
    {
        BanSnapshot[] active = bans.SnapshotActive(time.GetUtcNow())
            .OrderBy(static ban => ban.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy(static ban => ban.CreatedAt)
            .Take(100)
            .ToArray();
        if (active.Length == 0)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.ban.list-empty")
                    .WithDefaultText("There are no active bans."));
        }

        return CommandExecutionResult.WithMessages(active.Select(ban =>
            MessageDescriptor.Info(
                    "command.ban.list-entry",
                    ban.Id.Value.ToString("D"),
                    FormatSubject(ban.Subject, invocation.Principal),
                    FormatExpiry(ban.ExpiresAt),
                    ban.Reason)
                .WithDefaultText("{0} | {1} | expires {2} | {3}"))
            .ToArray());
    }

    private CommandExecutionResult Show(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count != 2 || !Guid.TryParse(invocation.Arguments[1], out Guid id))
        {
            return InvalidUsage("command.ban.show-usage", "Usage: ban show <id>.");
        }

        BanSnapshot? ban = bans.SnapshotActive(time.GetUtcNow())
            .FirstOrDefault(candidate => candidate.Id.Value == id);
        return ban is null
            ? Failure("command.ban.not-found", "Active ban not found.")
            : CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.ban.details",
                        ban.Id.Value.ToString("D"),
                        FormatSubject(ban.Subject, invocation.Principal),
                        ban.CreatedBy,
                        ban.CreatedAt,
                        FormatExpiry(ban.ExpiresAt),
                        ban.Reason,
                        ban.Revision)
                    .WithDefaultText("{0} | {1} | by {2} at {3:u} | expires {4} | reason: {5} | revision {6}"));
    }

    private async ValueTask<CommandExecutionResult> RevokeAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 1 || !Guid.TryParse(invocation.Arguments[0], out Guid id))
        {
            return InvalidUsage("command.unban.usage", "Usage: unban <id>.");
        }

        BanSnapshot? active = bans.SnapshotActive(time.GetUtcNow())
            .FirstOrDefault(candidate => candidate.Id.Value == id);
        if (active is null)
        {
            return Failure("command.ban.not-found", "Active ban not found.");
        }

        bool revoked = await bans.RevokeAsync(
            new GlobalBanRevoked(
                active.Id,
                invocation.Principal.Name,
                time.GetUtcNow(),
                checked(active.Revision + 1)),
            cancellationToken).ConfigureAwait(false);
        return revoked
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.unban.applied", id.ToString("D"))
                    .WithDefaultText("Ban {0} was revoked."))
            : Failure("command.unban.persist-failed", "Unable to revoke ban {0}.", id.ToString("D"));
    }

    private static bool TryParseKind(string value, out GlobalBanSubjectKind kind)
    {
        kind = value.Trim().ToLowerInvariant() switch
        {
            "name" or "player" => GlobalBanSubjectKind.PlayerName,
            "account" => GlobalBanSubjectKind.Account,
            "uuid" => GlobalBanSubjectKind.Uuid,
            "ip" => GlobalBanSubjectKind.IpAddress,
            "cidr" or "network" => GlobalBanSubjectKind.Network,
            _ => (GlobalBanSubjectKind)(-1)
        };
        return Enum.IsDefined(kind);
    }

    private static bool TryParseDuration(string value, out TimeSpan? duration)
    {
        duration = null;
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized is "permanent" or "perm" or "never" or "0")
        {
            return true;
        }

        if (!CommandDuration.TryParse(normalized, TimeSpan.FromDays(36500), out var finite)) return false;
        duration = finite;
        return true;
    }

    private static string FormatSubject(GlobalBanSubject subject, ICommandPrincipal principal)
    {
        bool sensitive = subject.Kind is GlobalBanSubjectKind.Uuid or
            GlobalBanSubjectKind.IpAddress or GlobalBanSubjectKind.Network;
        return sensitive && !principal.HasPermission("vega.moderation.sensitive")
            ? $"{subject.Kind}:<redacted>"
            : $"{subject.Kind}:{subject.Value}";
    }

    private static string FormatExpiry(DateTimeOffset? expiresAt) =>
        expiresAt?.ToString("u", global::System.Globalization.CultureInfo.InvariantCulture) ?? "never";

    private IReadOnlyList<PlayerSnapshot> FindPlayers(string rawSearch, WorldId worldId)
    {
        string search = rawSearch.Trim();
        var online = players.SnapshotOnlinePlayers().Where(player => player.WorldId == worldId).ToArray();
        if (search.StartsWith("tsi:", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(search[4..], out int slot)
                ? online.Where(player => player.Id.Slot == slot).ToArray()
                : Array.Empty<PlayerSnapshot>();
        }

        PlayerSnapshot[] exact = online
            .Where(player => string.Equals(player.Name, search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return exact.Length > 0
            ? exact
            : online
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
