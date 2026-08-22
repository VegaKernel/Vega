using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Core.Commands.Administration;

public sealed class GroupCommand(IAuthorizationService authorization) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "group",
        ["groups"],
        ["vega.groups.manage"]);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return Usage();
        }

        string action = invocation.Arguments[0].ToLowerInvariant();
        if (action == "list")
        {
            return List();
        }

        if (action == "show")
        {
            return Show(invocation);
        }

        if (action == "create" && invocation.Arguments.Count is 2 or 3)
        {
            string? parent = invocation.Arguments.Count == 3 ? invocation.Arguments[2] : null;
            if (parent is not null && !CanDelegate(invocation.Principal, parent))
            {
                return DelegationDenied();
            }

            return Mutation(await authorization.CreateGroupAsync(
                invocation.Arguments[1], parent, invocation.Principal.Name, cancellationToken).ConfigureAwait(false));
        }

        if (action == "delete" && invocation.Arguments.Count == 2)
        {
            if (!authorization.TryGetGroup(invocation.Arguments[1], out GroupSnapshot? group) || group is null)
            {
                return NotFound();
            }

            if (!CanDelegate(invocation.Principal, group.Name))
            {
                return DelegationDenied();
            }

            return Mutation(await authorization.DeleteGroupAsync(
                group.Name, group.Revision, invocation.Principal.Name, cancellationToken).ConfigureAwait(false));
        }

        if (action == "parent" && invocation.Arguments.Count == 3)
        {
            if (!authorization.TryGetGroup(invocation.Arguments[1], out GroupSnapshot? group) || group is null)
            {
                return NotFound();
            }

            string? parent = invocation.Arguments[2].Equals("none", StringComparison.OrdinalIgnoreCase)
                ? null
                : invocation.Arguments[2];
            if (!CanDelegate(invocation.Principal, group.Name) ||
                (parent is not null && !CanDelegate(invocation.Principal, parent)))
            {
                return DelegationDenied();
            }

            return Mutation(await authorization.SetParentAsync(
                group.Name, parent, group.Revision, invocation.Principal.Name, cancellationToken).ConfigureAwait(false));
        }

        if ((action is "grant" or "deny" or "revoke") && invocation.Arguments.Count == 3)
        {
            if (!authorization.TryGetGroup(invocation.Arguments[1], out GroupSnapshot? group) || group is null)
            {
                return NotFound();
            }

            string permission = invocation.Arguments[2];
            if (!invocation.Principal.IsServer && !invocation.Principal.HasPermission(permission))
            {
                return DelegationDenied();
            }

            PermissionEffect? effect = action switch
            {
                "grant" => PermissionEffect.Grant,
                "deny" => PermissionEffect.Deny,
                _ => null
            };
            return Mutation(await authorization.SetPermissionAsync(
                group.Name,
                permission,
                effect,
                group.Revision,
                invocation.Principal.Name,
                cancellationToken).ConfigureAwait(false));
        }

        return Usage();
    }

    private CommandExecutionResult List()
    {
        GroupSnapshot[] groups = authorization.SnapshotGroups().Take(200).ToArray();
        return CommandExecutionResult.WithMessages(groups.Select(group =>
            MessageDescriptor.Info(
                    "command.group.list-entry",
                    group.Name,
                    group.ParentName ?? "none",
                    group.Revision)
                .WithDefaultText("{0} | parent {1} | revision {2}"))
            .ToArray());
    }

    private CommandExecutionResult Show(CommandInvocation invocation)
    {
        if (invocation.Arguments.Count != 2 ||
            !authorization.TryGetGroup(invocation.Arguments[1], out GroupSnapshot? group) || group is null)
        {
            return NotFound();
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info(
                    "command.group.details",
                    group.Name,
                    group.ParentName ?? "none",
                    group.Permissions.Count == 0 ? "none" : string.Join(", ", group.Permissions),
                    group.DeniedPermissions.Count == 0 ? "none" : string.Join(", ", group.DeniedPermissions),
                    group.Revision)
                .WithDefaultText("{0} | parent {1} | grants {2} | denies {3} | revision {4}"));
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

    private static CommandExecutionResult Mutation(GroupMutationResult result) =>
        result.Succeeded
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success(
                        "command.group.mutation-applied",
                        result.After?.Name ?? result.Before?.Name ?? "group",
                        result.Code)
                    .WithDefaultText("Group {0}: {1}."))
            : CommandExecutionResult.Failed(
                "command.group.mutation-failed",
                MessageDescriptor.Error("command.group.mutation-failed", result.Code)
                    .WithDefaultText("Group operation failed: {0}."));

    private static CommandExecutionResult NotFound() => CommandExecutionResult.Failed(
        "command.group.not-found",
        MessageDescriptor.Error("command.group.not-found").WithDefaultText("Group not found."));

    private static CommandExecutionResult DelegationDenied() => CommandExecutionResult.Failed(
        "command.group.delegation-denied",
        MessageDescriptor.Error("command.group.delegation-denied")
            .WithDefaultText("You cannot delegate authority that you do not hold."));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.group.usage",
        [MessageDescriptor.Error("command.group.usage")
            .WithDefaultText("Usage: group list | show <name> | create <name> [parent] | delete <name> | parent <name> <parent|none> | grant|deny|revoke <name> <permission>.")]);
}
