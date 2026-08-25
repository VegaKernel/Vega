using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;

namespace Vega.Core.Authorization;

public sealed record PermissionCommandReference(
    string Command,
    string OwnerAssembly,
    string HandlerType);

public sealed record PermissionGroupReference(
    string Group,
    string Effect);

public sealed record PermissionInspectionSnapshot(
    string Permission,
    IReadOnlyList<PermissionCommandReference> Commands,
    IReadOnlyList<PermissionGroupReference> Groups);

public sealed record PermissionRedundancy(
    string Group,
    string Permission,
    string Effect,
    string InheritedFrom);

/// <summary>
/// Read-only diagnostics for Vega permissions. The service follows Vega's actual authorization
/// semantics: exact permission names plus the global '*' wildcard, with child decisions taking
/// precedence over parent decisions.
/// </summary>
public sealed class PermissionInspectionService(
    ICommandRegistrationCatalog commands,
    IAuthorizationService authorization,
    IAccountService accounts)
{
    public PermissionInspectionSnapshot Inspect(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        string requested = permission.Trim();

        PermissionCommandReference[] commandReferences = commands.SnapshotRegistrations()
            .Where(registration => registration.Descriptor.Permissions.Any(candidate =>
                PermissionDeclarationMatches(requested, candidate)))
            .Select(registration => new PermissionCommandReference(
                registration.Descriptor.Name,
                registration.OwnerAssembly,
                registration.HandlerType))
            .OrderBy(static reference => reference.Command, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        PermissionGroupReference[] groupReferences = authorization.SnapshotGroups()
            .SelectMany(group => DirectMatches(group, requested))
            .OrderBy(static reference => reference.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static reference => reference.Effect, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PermissionInspectionSnapshot(requested, commandReferences, groupReferences);
    }

    public IReadOnlyList<string> FormatGroupTree()
    {
        IReadOnlyList<GroupSnapshot> snapshot = authorization.SnapshotGroups();
        var groups = snapshot.ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
        var children = snapshot
            .GroupBy(static group => group.ParentName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        GroupSnapshot[] roots = snapshot
            .Where(group => group.ParentName is null || !groups.ContainsKey(group.ParentName))
            .OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (GroupSnapshot root in roots)
        {
            AppendTree(root, 0, children, visited, lines);
        }

        foreach (GroupSnapshot orphan in snapshot
            .Where(group => !visited.Contains(group.Name))
            .OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"! {orphan.Name} (cycle or unreachable parent chain)");
            AppendTree(orphan, 1, children, visited, lines);
        }

        return lines.AsReadOnly();
    }

    public IReadOnlyList<PermissionRedundancy> FindRedundantPermissions(string? groupName = null)
    {
        IReadOnlyList<GroupSnapshot> snapshot = authorization.SnapshotGroups();
        var groups = snapshot.ToDictionary(static group => group.Name, StringComparer.OrdinalIgnoreCase);
        IEnumerable<GroupSnapshot> selected = string.IsNullOrWhiteSpace(groupName)
            ? snapshot
            : snapshot.Where(group => group.Name.Equals(groupName.Trim(), StringComparison.OrdinalIgnoreCase));
        var redundancies = new List<PermissionRedundancy>();

        foreach (GroupSnapshot group in selected.OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string permission in group.Permissions.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                PermissionResolution inherited = ResolveFromParent(group.ParentName, permission, groups);
                if (inherited.Decision == PermissionDecision.Grant)
                {
                    redundancies.Add(new PermissionRedundancy(
                        group.Name,
                        permission,
                        "grant",
                        inherited.SourceGroup!));
                }
            }

            foreach (string permission in group.DeniedPermissions.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase))
            {
                PermissionResolution inherited = ResolveFromParent(group.ParentName, permission, groups);
                if (inherited.Decision == PermissionDecision.Deny)
                {
                    redundancies.Add(new PermissionRedundancy(
                        group.Name,
                        permission,
                        "deny",
                        inherited.SourceGroup!));
                }
            }
        }

        return redundancies.AsReadOnly();
    }

    public IReadOnlyList<AccountSnapshot> UsersInGroup(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        string requested = groupName.Trim();
        return accounts.SnapshotAccounts()
            .Where(account => account.GroupName.Equals(requested, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static account => account.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PermissionGroupReference> DirectMatches(GroupSnapshot group, string requested)
    {
        if (group.Permissions.Any(candidate => PermissionDeclarationMatches(requested, candidate)))
        {
            yield return new PermissionGroupReference(group.Name, "grant");
        }

        if (group.DeniedPermissions.Any(candidate => PermissionDeclarationMatches(requested, candidate)))
        {
            yield return new PermissionGroupReference(group.Name, "deny");
        }
    }

    private static bool PermissionDeclarationMatches(string requested, string declared) =>
        declared.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
        declared.Equals("*", StringComparison.Ordinal);

    private static PermissionResolution ResolveFromParent(
        string? parentName,
        string permission,
        IReadOnlyDictionary<string, GroupSnapshot> groups)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = parentName;
        while (current is not null && visited.Count < 64 && groups.TryGetValue(current, out GroupSnapshot? group))
        {
            if (!visited.Add(current))
            {
                return default;
            }

            if (group.DeniedPermissions.Contains(permission) || group.DeniedPermissions.Contains("*"))
            {
                return new PermissionResolution(PermissionDecision.Deny, group.Name);
            }

            if (group.Permissions.Contains(permission) || group.Permissions.Contains("*"))
            {
                return new PermissionResolution(PermissionDecision.Grant, group.Name);
            }

            current = group.ParentName;
        }

        return default;
    }

    private static void AppendTree(
        GroupSnapshot group,
        int depth,
        IReadOnlyDictionary<string?, GroupSnapshot[]> children,
        ISet<string> visited,
        ICollection<string> lines)
    {
        if (!visited.Add(group.Name))
        {
            lines.Add($"{new string(' ', depth * 2)}! {group.Name} (cycle)");
            return;
        }

        lines.Add(
            $"{new string(' ', depth * 2)}- {group.Name} " +
            $"[grants:{group.Permissions.Count}, denies:{group.DeniedPermissions.Count}]");
        if (!children.TryGetValue(group.Name, out GroupSnapshot[]? nested))
        {
            return;
        }

        foreach (GroupSnapshot child in nested)
        {
            AppendTree(child, depth + 1, children, visited, lines);
        }
    }

    private enum PermissionDecision
    {
        None,
        Grant,
        Deny
    }

    private readonly record struct PermissionResolution(
        PermissionDecision Decision,
        string? SourceGroup);
}
