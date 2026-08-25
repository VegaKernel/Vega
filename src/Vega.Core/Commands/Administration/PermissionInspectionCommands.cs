using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Core.Authorization;

namespace Vega.Core.Commands.Administration;

public static class PermissionInspectionCommandRegistration
{
    public static IDisposable Register(
        ICommandRegistry registry,
        ICommandRegistrationCatalog commandCatalog,
        IAuthorizationService authorization,
        IAccountService accounts)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(commandCatalog);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(accounts);

        var inspector = new PermissionInspectionService(commandCatalog, authorization, accounts);
        ICommandHandler[] handlers =
        [
            new PermissionCommand(inspector),
            new CheckPermissionsCommand(inspector, authorization),
            new GroupTreeCommand(inspector),
            new GroupUsersCommand(inspector, authorization)
        ];

        return new RegistrationSet(handlers.Select(registry.Register).ToArray());
    }

    private sealed class RegistrationSet(IDisposable[] registrations) : IDisposable
    {
        private IDisposable[]? owned = registrations;

        public void Dispose()
        {
            IDisposable[]? snapshot = Interlocked.Exchange(ref owned, null);
            if (snapshot is null)
            {
                return;
            }

            for (int index = snapshot.Length - 1; index >= 0; index--)
            {
                snapshot[index].Dispose();
            }
        }
    }
}

public sealed class PermissionCommand(PermissionInspectionService inspector) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "perm",
        ["permission"],
        ["vega.permissions.inspect"],
        Syntax: "perm <permission>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 1 || string.IsNullOrWhiteSpace(invocation.Arguments[0]))
        {
            return ValueTask.FromResult(Usage());
        }

        PermissionInspectionSnapshot snapshot = inspector.Inspect(invocation.Arguments[0]);
        var messages = new List<MessageDescriptor>
        {
            MessageDescriptor.Info("command.perm.header", snapshot.Permission)
                .WithDefaultText("Permission: {0}")
        };

        if (snapshot.Commands.Count == 0)
        {
            messages.Add(MessageDescriptor.Info("command.perm.commands-none")
                .WithDefaultText("Commands: none"));
        }
        else
        {
            foreach (PermissionCommandReference command in snapshot.Commands)
            {
                messages.Add(MessageDescriptor.Info(
                        "command.perm.command",
                        command.Command,
                        command.OwnerAssembly,
                        command.HandlerType)
                    .WithDefaultText("Command: {0} | module {1} | handler {2}"));
            }
        }

        string[] modules = snapshot.Commands
            .Select(static command => command.OwnerAssembly)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static module => module, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        messages.Add(MessageDescriptor.Info(
                "command.perm.modules",
                modules.Length == 0 ? "none" : string.Join(", ", modules))
            .WithDefaultText("Modules: {0}"));

        if (snapshot.Groups.Count == 0)
        {
            messages.Add(MessageDescriptor.Info("command.perm.groups-none")
                .WithDefaultText("Groups with a direct matching declaration: none"));
        }
        else
        {
            foreach (PermissionGroupReference group in snapshot.Groups)
            {
                messages.Add(MessageDescriptor.Info(
                        "command.perm.group",
                        group.Group,
                        group.Effect)
                    .WithDefaultText("Group: {0} | direct {1}"));
            }
        }

        if (snapshot.Commands.Count == 0 && snapshot.Groups.Count == 0)
        {
            messages.Add(MessageDescriptor.Info("command.perm.not-found")
                .WithDefaultText(
                    "No current command or direct group declaration references this permission. " +
                    "It may still belong to a non-command gameplay policy or plugin feature."));
        }

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(messages.ToArray()));
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.perm.usage",
        [MessageDescriptor.Error("command.perm.usage").WithDefaultText("Usage: perm <permission>")]);
}

public sealed class CheckPermissionsCommand(
    PermissionInspectionService inspector,
    IAuthorizationService authorization) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "checkperms",
        ["checkpermissions"],
        ["vega.permissions.inspect"],
        Syntax: "checkperms [group]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count > 1)
        {
            return ValueTask.FromResult(Usage());
        }

        string? group = invocation.Arguments.Count == 1 ? invocation.Arguments[0] : null;
        if (group is not null && !authorization.TryGetGroup(group, out _))
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.checkperms.group-not-found",
                MessageDescriptor.Error("command.checkperms.group-not-found", group)
                    .WithDefaultText("Group '{0}' was not found.")));
        }

        IReadOnlyList<PermissionRedundancy> duplicates = inspector.FindRedundantPermissions(group);
        if (duplicates.Count == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.checkperms.clean", group ?? "all groups")
                    .WithDefaultText("No redundant inherited permissions found in {0}.")));
        }

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            duplicates.Take(500).Select(duplicate =>
                MessageDescriptor.Info(
                        "command.checkperms.duplicate",
                        duplicate.Group,
                        duplicate.Effect,
                        duplicate.Permission,
                        duplicate.InheritedFrom)
                    .WithDefaultText("Group {0}: direct {1} '{2}' is already inherited from {3}."))
                .ToArray()));
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.checkperms.usage",
        [MessageDescriptor.Error("command.checkperms.usage").WithDefaultText("Usage: checkperms [group]")]);
}

public sealed class GroupTreeCommand(PermissionInspectionService inspector) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "grouptree",
        ["grouphierarchy"],
        ["vega.permissions.inspect"]);

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0)
        {
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.grouptree.usage",
                [MessageDescriptor.Error("command.grouptree.usage").WithDefaultText("Usage: grouptree")]));
        }

        IReadOnlyList<string> lines = inspector.FormatGroupTree();
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            (lines.Count == 0 ? ["No groups configured."] : lines.Take(500))
                .Select(line => MessageDescriptor.Info("command.grouptree.line", line).WithDefaultText("{0}"))
                .ToArray()));
    }
}

public sealed class GroupUsersCommand(
    PermissionInspectionService inspector,
    IAuthorizationService authorization) : ICommandHandler
{
    private const int UsersPerPage = 20;

    public CommandDescriptor Descriptor { get; } = new(
        "groupusers",
        ["gu"],
        ["vega.permissions.inspect"],
        Syntax: "groupusers <group> [page]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2)
        {
            return ValueTask.FromResult(Usage());
        }

        string groupName = invocation.Arguments[0];
        if (!authorization.TryGetGroup(groupName, out GroupSnapshot? group) || group is null)
        {
            return ValueTask.FromResult(CommandExecutionResult.Failed(
                "command.groupusers.group-not-found",
                MessageDescriptor.Error("command.groupusers.group-not-found", groupName)
                    .WithDefaultText("Group '{0}' was not found.")));
        }

        int page = 1;
        if (invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
        {
            return ValueTask.FromResult(Usage());
        }

        IReadOnlyList<AccountSnapshot> users = inspector.UsersInGroup(group.Name);
        if (users.Count == 0)
        {
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.groupusers.none", group.Name)
                    .WithDefaultText("No users are assigned directly to group {0}.")));
        }

        int totalPages = (users.Count + UsersPerPage - 1) / UsersPerPage;
        page = Math.Min(page, totalPages);
        string names = string.Join(", ", users
            .Skip((page - 1) * UsersPerPage)
            .Take(UsersPerPage)
            .Select(static account => account.Name));

        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info(
                    "command.groupusers.page",
                    group.Name,
                    page,
                    totalPages,
                    users.Count,
                    names)
                .WithDefaultText("Users in group {0} | page {1}/{2} | total {3}: {4}")));
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.groupusers.usage",
        [MessageDescriptor.Error("command.groupusers.usage")
            .WithDefaultText("Usage: groupusers <group> [page]")]);
}
