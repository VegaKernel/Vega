using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using System.Globalization;

namespace Vega.Core.Commands.Administration;

public sealed class AccountCommand(
    IAccountService accounts,
    IAuthorizationService authorization,
    IRoleLeaseService? roleLeases = null,
    TimeProvider? timeProvider = null) : ICommandHandler
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public CommandDescriptor Descriptor { get; } = new(
        "account",
        ["user"],
        ["vega.accounts.manage"],
        Syntax: "account list | show <name> | create <name> <password> [group] | delete <name> | group <name> <group> | password <name> <new-password> | temp <name> <group> <duration> | temp-clear <name> | temp-list");

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
            "create" => await CreateAsync(invocation, cancellationToken).ConfigureAwait(false),
            "delete" => await DeleteAsync(invocation, cancellationToken).ConfigureAwait(false),
            "group" => await SetGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "password" => await SetPasswordAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp" => await GrantTemporaryGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp-clear" => await RevokeTemporaryGroupAsync(invocation, cancellationToken).ConfigureAwait(false),
            "temp-list" => ListTemporaryGroups(),
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
        if (!CanDelegate(invocation.Principal, group))
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
            !CanDelegate(invocation.Principal, invocation.Arguments[2]))
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
            before.Name,
            invocation.Arguments[2],
            before.Revision,
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return Mutation(result, "Changed password for account {0}.");
    }

    private async ValueTask<CommandExecutionResult> GrantTemporaryGroupAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (roleLeases is null || invocation.Arguments.Count != 4 ||
            !TryParseDuration(invocation.Arguments[3], out TimeSpan duration))
        {
            return Usage();
        }

        AccountSnapshot? account = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return Failure("command.account.not-found", "Account not found.");
        }

        string group = invocation.Arguments[2];
        if (!CanDelegate(invocation.Principal, account.GroupName) || !CanDelegate(invocation.Principal, group))
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
            now.Add(duration),
            invocation.Principal.Name,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.After is not null
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.account.temp-granted",
                        account.Name,
                        result.After.GroupName,
                        result.After.ExpiresAt.ToString("u"))
                    .WithDefaultText("Assigned temporary group {1} to {0} until {2}."))
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

        AccountSnapshot? account = await accounts.FindByNameAsync(invocation.Arguments[1], cancellationToken)
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
            !CanDelegate(invocation.Principal, lease.GroupName))
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
                    .WithDefaultText("Revoked temporary group {1} from {0}."))
            : Failure("command.account.temp-failed", "Temporary group operation failed: {0}.", result.Code);
    }

    private CommandExecutionResult ListTemporaryGroups()
    {
        if (roleLeases is null)
        {
            return Usage();
        }

        IReadOnlyDictionary<AccountId, AccountSnapshot> accountById = accounts.SnapshotAccounts()
            .ToDictionary(static account => account.Id);
        RoleLeaseSnapshot[] leases = roleLeases.SnapshotActive(clock.GetUtcNow()).Take(200).ToArray();
        return leases.Length == 0
            ? Info("command.account.temp-list-empty", "There are no active temporary groups.")
            : CommandExecutionResult.WithMessages(leases.Select(lease =>
                MessageDescriptor.Info(
                        "command.account.temp-list-entry",
                        accountById.TryGetValue(lease.AccountId, out AccountSnapshot? account)
                            ? account.Name
                            : lease.AccountId.Value.ToString(CultureInfo.InvariantCulture),
                        lease.GroupName,
                        lease.ExpiresAt.ToString("u"),
                        lease.IssuedBy)
                    .WithDefaultText("{0} | group {1} | expires {2} | issued by {3}"))
                .ToArray());
    }

    private static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        double multiplier = value[^1] switch
        {
            's' or 'S' => 1,
            'm' or 'M' => 60,
            'h' or 'H' => 60 * 60,
            'd' or 'D' => 24 * 60 * 60,
            _ => 0
        };
        return multiplier > 0 &&
            double.TryParse(value[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double units) &&
            units > 0 && units * multiplier <= TimeSpan.FromDays(365).TotalSeconds &&
            TrySeconds(units * multiplier, out duration);
    }

    private static bool TrySeconds(double seconds, out TimeSpan duration)
    {
        try
        {
            duration = TimeSpan.FromSeconds(seconds);
            return duration > TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            duration = default;
            return false;
        }
    }

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
            .WithDefaultText("Usage: account list | show <name> | create <name> <password> [group] | delete <name> | group <name> <group> | password <name> <new-password> | temp <name> <group> <duration: 30m|2h|7d> | temp-clear <name> | temp-list.")]);

    private static CommandExecutionResult Info(string key, string text) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Info(key).WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text, params object?[] arguments) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, arguments).WithDefaultText(text));
}
