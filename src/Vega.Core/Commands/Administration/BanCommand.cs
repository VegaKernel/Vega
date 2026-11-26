using System.Net;
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
    TimeProvider? timeProvider = null) : ICommandHandler
{
    private const string BanPermission = "vega.admin.ban";
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "ban",
        ["unban"],
        [BanPermission],
        Syntax: "ban <player> [reason] | ban add <name|account|uuid|ip|cidr> <value> <duration|permanent> [reason] | ban list | ban show <id> | unban <id>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
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

        if (action == "show")
        {
            return Show(invocation);
        }

        if (action is "revoke" or "remove")
        {
            return await RevokeAsync(invocation with
            {
                Arguments = invocation.Arguments.Skip(1).ToArray()
            }, cancellationToken).ConfigureAwait(false);
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
        if (normalized is "permanent" or "perm" or "never")
        {
            return true;
        }

        if (normalized.Length < 2 ||
            !double.TryParse(
                normalized[..^1],
                global::System.Globalization.NumberStyles.AllowDecimalPoint,
                global::System.Globalization.CultureInfo.InvariantCulture,
                out double amount) ||
            amount <= 0)
        {
            return false;
        }

        double days = normalized[^1] switch
        {
            'm' => amount / (24 * 60),
            'h' => amount / 24,
            'd' => amount,
            'w' => amount * 7,
            _ => double.NaN
        };
        if (!double.IsFinite(days) || days > 36500)
        {
            return false;
        }

        duration = TimeSpan.FromDays(days);
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
