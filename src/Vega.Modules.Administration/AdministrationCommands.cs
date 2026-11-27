using System.Globalization;
using System.Net;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

public sealed class AccountCommand(
    IAccountService accounts,
    IAuthorizationService authorization,
    IRoleLeaseService? roleLeases = null,
    TimeProvider? timeProvider = null,
    IReadOnlyDictionary<string, HashSet<string>>? groupAssignments = null) : ICommandHandler
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "account",
        ["user"],
        ["vega.accounts.manage"],
        Syntax: "account list | show <name> | create|add <name> <password> [group] | delete|del <name> | group <name> <group> | password <name> <new-password> | temp <name> <group> <duration> | temp-clear <name> | temp-list | help");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return Usage();
        }

        return invocation.Arguments[0].ToLowerInvariant() switch
        {
            "list" => List(),
            "show" => await ShowAsync(invocation, cancellationToken).ConfigureAwait(false),
            "create" or "add" => await CreateAsync(invocation, cancellationToken).ConfigureAwait(false),
            "delete" or "del" => await DeleteAsync(invocation, cancellationToken).ConfigureAwait(false),
            "group" => await SetGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "password" => await SetPasswordAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp" => await GrantTemporaryGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp-clear" => await RevokeTemporaryGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp-list" => ListTemporaryGroups(invocation),
            "temp-info" => await TemporaryGroupInfoAsync(invocation, cancellationToken).ConfigureAwait(false),
            "help" when invocation.Arguments.Count == 1 => Info("command.account.help", Descriptor.Syntax),
            _ => Usage()
        };
    }

    private CommandExecutionResult List()
    {
        AccountSnapshot[] snapshot = accounts.SnapshotAccounts().Take(200).ToArray();
        return snapshot.Length == 0
            ? Info("command.account.list-empty", "There are no accounts.")
            : CommandExecutionResult.WithMessages(snapshot.Select(account =>
                MessageDescriptor.Info(
                        "command.account.list-entry",
                        account.Id.Value,
                        account.Name,
                        account.GroupName,
                        account.Culture ?? "default",
                        account.Revision)
                    .WithDefaultText("{0} | {1} | group {2} | culture {3} | revision {4}"))
                .ToArray());
    }

    private async ValueTask<CommandExecutionResult> ShowAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2)
        {
            return Usage();
        }

        AccountSnapshot? account = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        return account is null
            ? Failure("command.account.not-found", "Account not found.")
            : CommandExecutionResult.WithMessages(
                MessageDescriptor.Info(
                        "command.account.details",
                        account.Id.Value,
                        account.Name,
                        account.GroupName,
                        account.Culture ?? "default",
                        account.LastSeenUtc?.ToString("u") ?? "never",
                        account.Revision)
                    .WithDefaultText("{0} | {1} | group {2} | culture {3} | last seen {4} | revision {5}"));
    }

    private async ValueTask<CommandExecutionResult> CreateAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 3 or > 4)
        {
            return Usage();
        }

        string group = invocation.Arguments.Count == 4 ? invocation.Arguments[3] : "default";
        if (!CanDelegate(invocation.Principal, group) || !CanAssignGroup(invocation.Principal, group))
        {
            return Failure(
                "command.account.delegation-denied",
                "You cannot assign group '{0}' because it grants authority you do not hold.",
                group);
        }

        AccountMutationResult result = await accounts.CreateAsync(
            invocation.Arguments[1],
            invocation.Arguments[2],
            group,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return Mutation(result, "Created account {0} in group {1}.");
    }

    private async ValueTask<CommandExecutionResult> DeleteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2)
        {
            return Usage();
        }

        AccountSnapshot? before = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        if (!CanDelegate(invocation.Principal, before.GroupName))
        {
            return Failure("command.account.delegation-denied", "You cannot delete an account above your authority.");
        }

        AccountMutationResult result = await accounts.DeleteAsync(
            before.Name,
            before.Revision,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return Mutation(result, "Deleted account {0}.");
    }

    private async ValueTask<CommandExecutionResult> SetGroupAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 3)
        {
            return Usage();
        }

        AccountSnapshot? before = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        if (!CanDelegate(invocation.Principal, before.GroupName) ||
            !CanDelegate(invocation.Principal, invocation.Arguments[2]) ||
            !CanAssignGroup(invocation.Principal, before.GroupName, invocation.Arguments[2]))
        {
            return Failure("command.account.delegation-denied", "You cannot assign or replace a group above your authority.");
        }

        AccountMutationResult result = await accounts.SetGroupAsync(
            before.Name,
            invocation.Arguments[2],
            before.Revision,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return Mutation(result, "Assigned account {0} to group {1}.");
    }

    private async ValueTask<CommandExecutionResult> SetPasswordAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 3)
        {
            return Usage();
        }

        AccountSnapshot? before = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (before is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        if (!CanDelegate(invocation.Principal, before.GroupName))
        {
            return Failure("command.account.delegation-denied", "You cannot change this account's password.");
        }

        if (invocation.Arguments[2].Length is < 8 or > 1024)
        {
            return Failure("account.invalid-password", "Password length must be between 8 and 1024 characters.");
        }

        AccountMutationResult result = await accounts.ChangePasswordAsync(
            name: before.Name,
            password: invocation.Arguments[2],
            expectedRevision: before.Revision,
            actor: invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return Mutation(result, "Changed password for account {0}.");
    }

    private async ValueTask<CommandExecutionResult> GrantTemporaryGroupAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        TimeSpan duration = default;
        if (roleLeases is null || invocation.Arguments.Count is < 3 or > 4 ||
            invocation.Arguments.Count == 4 && !TryParseDuration(invocation.Arguments[3], out duration))
        {
            return Usage();
        }

        AccountSnapshot? account = await ResolveAccountAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        string group = invocation.Arguments[2];
        if (!CanDelegate(invocation.Principal, account.GroupName) || !CanDelegate(invocation.Principal, group) ||
            !CanAssignGroup(invocation.Principal, account.GroupName, group) ||
            (roleLeases.FindActive(account.Id, clock.GetUtcNow()) is { } existing &&
             (!CanDelegate(invocation.Principal, existing.GroupName) || !CanAssignGroup(invocation.Principal, existing.GroupName))))
        {
            return Failure(
                "command.account.delegation-denied",
                "You cannot assign temporary group '{0}' because it grants authority you do not hold.",
                group);
        }

        DateTimeOffset now = clock.GetUtcNow();
        RoleLeaseMutationResult result = await roleLeases.GrantAsync(
            account.Id,
            group,
            now,
            invocation.Arguments.Count == 3 ? DateTimeOffset.MaxValue : now.Add(duration),
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.After is not null
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.account.temp-granted",
                        account.Name,
                        result.After.GroupName,
                        FormatExpiration(result.After.ExpiresAt))
                    .WithDefaultText(IsTempGroupInvocation(invocation) ? "A new temporary group has been added." : "Assigned temporary group {1} to {0} until {2}."))
            : Failure("command.account.temp-failed", "Temporary group operation failed: {0}.", result.Code);
    }

    private async ValueTask<CommandExecutionResult> RevokeTemporaryGroupAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (roleLeases is null || invocation.Arguments.Count != 2)
        {
            return Usage();
        }

        AccountSnapshot? account = await ResolveAccountAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        RoleLeaseSnapshot? lease = roleLeases.FindActive(account.Id, clock.GetUtcNow());
        if (lease is null)
        {
            return Failure("role-lease.not-found", "This account has no active temporary group.");
        }

        if (!CanDelegate(invocation.Principal, account.GroupName) ||
            !CanDelegate(invocation.Principal, lease.GroupName) ||
            !CanAssignGroup(invocation.Principal, account.GroupName, lease.GroupName))
        {
            return Failure("command.account.delegation-denied", "You cannot revoke this temporary group.");
        }

        RoleLeaseMutationResult result = await roleLeases.RevokeAsync(
            lease.Id,
            lease.Revision,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.account.temp-revoked", account.Name, lease.GroupName)
                    .WithDefaultText(IsTempGroupInvocation(invocation) ? "The temporary group has been deleted." : "Revoked temporary group {1} from {0}."))
            : Failure("command.account.temp-failed", "Temporary group operation failed: {0}.", result.Code);
    }

    internal ValueTask<AccountSnapshot?> ResolveAccountAsync(string value, CancellationToken token) =>
        int.TryParse(value, out int id)
            ? ValueTask.FromResult(accounts.SnapshotAccounts().FirstOrDefault(account => account.Id.Value == id))
            : accounts.FindByNameAsync(value, token);

    internal ValueTask<AccountSnapshot?> FindAccountByNameAsync(string name, CancellationToken token) => accounts.FindByNameAsync(name, token);

    internal async ValueTask<CommandExecutionResult> TemporaryGroupInfoAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (roleLeases is null || invocation.Arguments.Count != 2) return Usage();
        bool ownStatus = invocation.Name.Equals("tempstatus", StringComparison.OrdinalIgnoreCase) || invocation.Name.Equals("temps", StringComparison.OrdinalIgnoreCase) || invocation.Name.Equals("ts", StringComparison.OrdinalIgnoreCase);
        AccountSnapshot? account = await (ownStatus ? accounts.FindByNameAsync(invocation.Arguments[1], token) : ResolveAccountAsync(invocation.Arguments[1], token)).ConfigureAwait(false);
        if (account is null) return Failure("command.account.not-found", "Invalid account.");
        var lease = roleLeases.FindActive(account.Id, clock.GetUtcNow());
        if (lease is null) return Failure("role-lease.not-found", "You don't have a temp group.");
        if (ownStatus)
        {
            var prefix = authorization.SnapshotGroups().FirstOrDefault(group => group.Name.Equals(lease.GroupName, StringComparison.OrdinalIgnoreCase))?.Presentation.Prefix;
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.tempstatus.info", string.IsNullOrEmpty(prefix) ? lease.GroupName : prefix, FormatExpiration(lease.ExpiresAt)).WithDefaultText("TempGroup: {0} | Expiration: {1}"));
        }
        return CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.tempgroup.info", account.Id.Value,
            account.Name, lease.GroupName, FormatExpiration(lease.ExpiresAt))
            .WithDefaultText("AccountID: {0} - AccountName: {1} | TempGroup: {2} | Expiration: {3}"));
    }

    internal static string FormatExpiration(DateTimeOffset expiry) => expiry == DateTimeOffset.MaxValue ? "Never" : expiry.ToString("u");

    private static bool IsTempGroupInvocation(CommandInvocation invocation) =>
        invocation.Name.Equals("tg", StringComparison.OrdinalIgnoreCase) || invocation.Name.Equals("tempg", StringComparison.OrdinalIgnoreCase) || invocation.Name.Equals("tempgroup", StringComparison.OrdinalIgnoreCase);

    private bool CanAssignGroup(ICommandPrincipal principal, params string[] targetGroups)
    {
        if (groupAssignments is null || principal.IsServer) return true;
        if (principal is not IPlayerCommandPrincipal { AccountName: { } name }) return false;
        var account = accounts.SnapshotAccounts().FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (account is null) return false;
        string source = roleLeases?.FindActive(account.Id, clock.GetUtcNow())?.GroupName ?? account.GroupName;
        var allowed = groupAssignments.FirstOrDefault(entry => entry.Key.Equals(source, StringComparison.OrdinalIgnoreCase)).Value;
        return allowed is not null && (allowed.Contains("*") || targetGroups.All(target => allowed.Contains(target, StringComparer.OrdinalIgnoreCase)));
    }

    private CommandExecutionResult ListTemporaryGroups(CommandInvocation invocation)
    {
        if (roleLeases is null)
        {
            return Usage();
        }

        IReadOnlyDictionary<AccountId, AccountSnapshot> accountById = accounts.SnapshotAccounts()
            .ToDictionary(static account => account.Id);
        int page = 1;
        if (invocation.Arguments.Count > 2 || (invocation.Arguments.Count == 2 && (!int.TryParse(invocation.Arguments[1], out page) || page < 1 || page > int.MaxValue / 10)))
            return Usage();
        bool compatibility = IsTempGroupInvocation(invocation);
        var all = roleLeases.SnapshotActive(clock.GetUtcNow());
        RoleLeaseSnapshot[] leases = all.Skip((page - 1) * 10).Take(compatibility ? 10 : 200).ToArray();
        if (compatibility)
        {
            if (leases.Length == 0) return Info("command.tempgroup.empty", "There are currently no tempgroup accounts.");
            int pages = (all.Count - 1) / 10 + 1;
            var messages = new List<MessageDescriptor> { MessageDescriptor.Info("command.tempgroup.list.header", page, pages).WithDefaultText("Tempered group list ({0}/{1})") };
            messages.AddRange(leases.Select(lease => MessageDescriptor.Info("command.tempgroup.list.entry", lease.AccountId.Value, lease.GroupName, FormatExpiration(lease.ExpiresAt)).WithDefaultText("AccountID: {0} | TempGroup: {1} | Expiration: {2}")));
            if (page < pages) messages.Add(MessageDescriptor.Info("command.tempgroup.list.next", page + 1).WithDefaultText("Type /tg list {0} for more."));
            return CommandExecutionResult.WithMessages([.. messages]);
        }
        return leases.Length == 0
            ? Info("command.account.temp-list-empty", "There are no active temporary groups.")
            : CommandExecutionResult.WithMessages(leases.Select(lease =>
                MessageDescriptor.Info(
                        "command.account.temp-list-entry",
                        accountById.TryGetValue(lease.AccountId, out AccountSnapshot? account)
                            ? account.Name
                            : lease.AccountId.Value.ToString(CultureInfo.InvariantCulture),
                        lease.GroupName,
                        FormatExpiration(lease.ExpiresAt),
                        lease.IssuedBy)
                    .WithDefaultText("{0} | group {1} | expires {2} | issued by {3}"))
                .ToArray());
    }

    private static bool TryParseDuration(string text, out TimeSpan duration) =>
        CommandDuration.TryParse(text, TimeSpan.FromDays(365), out duration);

    private bool CanDelegate(ICommandPrincipal principal, string groupName)
    {
        if (principal.IsServer)
        {
            return true;
        }

        var groups = authorization.SnapshotGroups()
            .ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = groupName;
        while (current is not null && visited.Add(current) && groups.TryGetValue(current, out GroupSnapshot? group))
        {
            if (group.Permissions.Any(permission => !principal.HasPermission(permission)))
            {
                return false;
            }

            current = group.ParentName;
        }

        return current is null;
    }

    private static CommandExecutionResult Mutation(AccountMutationResult result, string successText) =>
        result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.account.mutation-applied",
                        result.After?.Name ?? result.Before?.Name ?? "account",
                        result.After?.GroupName ?? result.Before?.GroupName ?? "none")
                    .WithDefaultText(successText))
            : Failure("command.account.mutation-failed", "Account operation failed: {0}.", result.Code);

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.account.usage",
        [MessageDescriptor.Error("command.account.usage")
            .WithDefaultText("Usage: account list | show <name> | create|add <name> <password> [group] | delete|del <name> | group <name> <group> | password <name> <new-password> | temp <name> <group> <duration: 30m|2h|7d> | temp-clear <name> | temp-list | help.")]);

    private static CommandExecutionResult Info(string key, string text) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Info(key).WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text, params object?[] arguments) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, arguments).WithDefaultText(text));
}

public sealed class UserInfoCommand(
    IPlayerDirectory players,
    IPlayerAccountSessionService? accounts = null,
    IRoleLeaseService? roleLeases = null) : ICommandHandler
{
    private const string Source = "vega.userinfo";
    public CommandDescriptor Descriptor { get; } = new(
        "userinfo",
        ["ui"],
        ["vega.players.inspect", "tshock.admin.userinfo"],
        Syntax: "userinfo <player>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 1)
        {
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage,
                "command.userinfo.usage", [Error("command.userinfo.usage", "Invalid syntax. Proper syntax: /userinfo <player>.")]));
        }

        var matches = PlayerTargetLookup.FindByNameOrSlot(players, invocation.WorldId, invocation.Arguments[0]);
        if (matches.Count == 0) return ValueTask.FromResult(Failure("command.userinfo.invalid_player", "Invalid player."));
        if (matches.Count > 1) return ValueTask.FromResult(Failure("command.userinfo.multiple_matches", "Multiple players matched: {0}.",
            string.Join(", ", matches.Select(player => player.Name))));
        var target = matches[0];
        // No inventory capture is needed for TShock-style userinfo. Revalidate the
        // observed session before exposing connection metadata or account data.
        if (target.SessionId is not { } session || !players.TryGet(target.Id, out var player) || player is null ||
            player.SessionId != session || player.WorldId != invocation.WorldId)
        {
            return ValueTask.FromResult(Failure("command.userinfo.invalid_player", "Invalid player."));
        }

        var messages = new List<MessageDescriptor>();
        bool sensitive = invocation.Principal.HasPermission("vega.players.sensitive");
        if (sensitive || invocation.Principal.HasPermission("tshock.admin.userinfo.ip"))
            messages.Add(Success("command.userinfo.ip", "IP Address: {0}.", IpAddress(player.Connection?.EffectiveEndpoint ?? player.RemoteEndpoint)));
        if (sensitive || invocation.Principal.HasPermission("tshock.accountinfo.details.uuid"))
            messages.Add(Success("command.userinfo.uuid", "Short UUID: {0}", ShortUuid(player.Connection?.ClientUuid)));
        if (accounts is not null && accounts.TryGetBinding(player, out var binding) &&
            accounts.TryRunWithBinding(binding, () => ReadIdentity(binding), out var identity) && identity is { } current)
            messages.Add(Success("command.userinfo.account", " -> Logged-in as: {0}; in group {1}.", current.Name, current.Group));
        messages.Add(player.Platform.Kind switch
        {
            ClientPlatformKind.Unknown => Success("command.userinfo.platform_unknown", "Platform: Unknown"),
            ClientPlatformKind.Unrecognized => Success("command.userinfo.platform_unrecognized", "Platform: Unrecognized"),
            ClientPlatformKind.Pc => Success("command.userinfo.platform", "Platform: {0}", "PC"),
            _ => Success("command.userinfo.platform", "Platform: {0}", PlatformCommand.Label(player.Platform.Kind))
        });
        return ValueTask.FromResult(CommandExecutionResult.WithMessages([.. messages]));
    }

    private (string Name, string Group)? ReadIdentity(PlayerAccountBinding binding)
    {
        if (accounts is null || !accounts.TryGetAccount(binding.PlayerId, out var account) || account is null || account.Id != binding.AccountId)
            return null;
        string group = roleLeases?.FindActive(account.Id, DateTimeOffset.UtcNow)?.GroupName ?? account.GroupName;
        return (account.Name, group);
    }

    private static string IpAddress(string? endpoint) => IPAddress.TryParse(endpoint, out var address) ? address.ToString() :
        endpoint is not null && IPEndPoint.TryParse(endpoint, out var parsed) ? parsed.Address.ToString() : "N/A";

    internal static string ShortUuid(string? uuid) => string.IsNullOrEmpty(uuid) ? "N/A" : uuid.Length <= 17 ? uuid : $"{uuid[..8]}…{uuid[^8..]}";

    private static MessageDescriptor Success(string key, string text, params object?[] args) =>
        MessageDescriptor.Success(key, args).WithSource(Source).WithDefaultText(text);
    private static MessageDescriptor Error(string key, string text, params object?[] args) =>
        MessageDescriptor.Error(key, args).WithSource(Source).WithDefaultText(text);
    private static CommandExecutionResult Failure(string code, string text, params object?[] args) =>
        CommandExecutionResult.Failed(code, Error(code, text, args));

    public static IDisposable RegisterRussianCatalog(ILocalizationService localization) => localization.RegisterCatalog(Source, "ru-RU",
        new Dictionary<string, string>
        {
            ["command.userinfo.usage"] = "Неверный синтаксис. Правильно: /userinfo <игрок>.",
            ["command.userinfo.invalid_player"] = "Игрок не найден.",
            ["command.userinfo.multiple_matches"] = "Найдено несколько игроков: {0}.",
            ["command.userinfo.ip"] = "IP-адрес: {0}.",
            ["command.userinfo.uuid"] = "Короткий UUID: {0}",
            ["command.userinfo.account"] = " -> Авторизован как: {0}; группа: {1}.",
            ["command.userinfo.platform"] = "Платформа: {0}",
            ["command.userinfo.platform_unknown"] = "Платформа: Неизвестно",
            ["command.userinfo.platform_unrecognized"] = "Платформа: Не распознана"
        });
}

public sealed class InventoryCommand(
    IPlayerDirectory players,
    IPlayerInventoryService inventories) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "invsee",
        ["inventory"],
        ["vega.inventory.inspect"],
        Syntax: "invsee <player> [set <revision> <slot> <item-type> <stack> [prefix]]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 1)
        {
            return await InspectAsync(invocation.Arguments[0], invocation.WorldId, cancellationToken).ConfigureAwait(false);
        }

        if (invocation.Arguments.Count is < 6 or > 7 ||
            !string.Equals(invocation.Arguments[1], "set", StringComparison.OrdinalIgnoreCase) ||
            !invocation.Principal.HasPermission("vega.inventory.mutate") ||
            !long.TryParse(invocation.Arguments[2], out long revision) ||
            !int.TryParse(invocation.Arguments[3], out int slot) ||
            !int.TryParse(invocation.Arguments[4], out int itemType) ||
            !int.TryParse(invocation.Arguments[5], out int stack) ||
            (invocation.Arguments.Count == 7 && !byte.TryParse(invocation.Arguments[6], out _)))
        {
            return Usage();
        }

        if (!players.TryFindExact(invocation.Arguments[0], out PlayerSnapshot? target) || target is null || target.WorldId != invocation.WorldId)
        {
            return Failure("player.not_found", "Player not found.");
        }

        byte prefix = invocation.Arguments.Count == 7 ? byte.Parse(invocation.Arguments[6]) : (byte)0;
        InventoryMutationResult result = await inventories.ReplaceSlotAsync(
            target,
            revision,
            new InventorySlotReplacement(slot, itemType, stack, prefix),
            cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.invsee.updated")
                    .WithDefaultText($"{target.Name} inventory slot {slot} updated; revision={result.Snapshot!.Revision}."))
            : Failure(result.ErrorCode ?? "inventory.failed", result.ErrorCode ?? "Inventory update failed.");
    }

    private async ValueTask<CommandExecutionResult> InspectAsync(
        string name,
        Vega.Contracts.Worlds.WorldId worldId,
        CancellationToken cancellationToken)
    {
        if (!players.TryFindExact(name, out PlayerSnapshot? target) || target is null || target.WorldId != worldId)
        {
            return Failure("player.not_found", "Player not found.");
        }

        PlayerInventorySnapshot? snapshot = await inventories
            .CaptureAsync(target, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return Failure("player.not_found", "Player is no longer online.");
        }

        string items = string.Join(
            ", ",
            snapshot.Items
                .Where(static item => item.ItemType != 0 && item.Stack > 0)
                .Select(static item => $"{item.Slot}:{item.Name}x{item.Stack}")
                .Take(40));
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.invsee.snapshot")
                .WithDefaultText(
                    $"{snapshot.PlayerName} inventory revision={snapshot.Revision}: " +
                    (items.Length == 0 ? "empty" : items)));
    }

    private static CommandExecutionResult Usage() => Failure(
        "command.invsee.usage",
        "Usage: invsee <player> [set <revision> <slot> <item-type> <stack> [prefix]]");

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
