using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands.Administration;

public sealed class AccountCommand(
    IAccountService accounts,
    IAuthorizationService authorization) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "account",
        ["user"],
        ["vega.accounts.manage"]);

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
            .WithDefaultText("Usage: account list | show <name> | create <name> <password> [group] | delete <name> | group <name> <group> | password <name> <new-password>.")]);

    private static CommandExecutionResult Info(string key, string text) =>
        CommandExecutionResult.WithMessages(MessageDescriptor.Info(key).WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text, params object?[] arguments) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code, arguments).WithDefaultText(text));
}
