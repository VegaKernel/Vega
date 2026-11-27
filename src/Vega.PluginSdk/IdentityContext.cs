using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Minimal account directory identity, never password material or private profile metadata.</summary>
public sealed record PluginAccountIdentity(AccountId Id, string Name, string GroupName);

public sealed record PluginAccountChanged(string Operation, string Actor, PluginAccountIdentity? Before, PluginAccountIdentity? After);
public sealed record PluginGroupChanged(string Operation, string Actor, GroupSnapshot? Before, GroupSnapshot? After);
public sealed record PluginIdentityMutationResult(bool Succeeded, string Code, PluginAccountIdentity? Account = null, RoleLeaseSnapshot? TemporaryGroup = null);

/// <summary>Host identity queries, committed observations and explicitly delegated account group assignment.</summary>
public interface IPluginIdentity
{
    bool AccountsAvailable { get; }
    bool GroupsAvailable { get; }
    bool AuthenticatedPermissionsAvailable { get; }
    bool AccountChangesAvailable => false;
    bool GroupChangesAvailable => false;
    bool TemporaryGroupChangesAvailable => false;
    /// <summary>Explicit per-plugin server configuration permits assignment of specific groups.</summary>
    bool GroupAssignmentsAvailable => false;
    ValueTask<PluginIdentityMutationResult> AssignGroupAsync(AccountId account, string group, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    ValueTask<PluginIdentityMutationResult> GrantTemporaryGroupAsync(AccountId account, string group, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    ValueTask<PluginIdentityMutationResult> RevokeOwnTemporaryGroupAsync(AccountId account, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    void OnAccountChanged(Action<PluginAccountChanged> handler) => throw new NotSupportedException();
    void OnGroupChanged(Action<PluginGroupChanged> handler) => throw new NotSupportedException();
    void OnTemporaryGroupChanged(Action<RoleLeaseChange> handler) => throw new NotSupportedException();
    ValueTask<RoleLeaseSnapshot?> FindTemporaryGroupAsync(AccountId account, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    ValueTask<PluginAccountIdentity?> FindAccountAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<PluginAccountIdentity?> FindAccountByIdAsync(AccountId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    bool TryGetAuthenticatedAccount(PlayerSnapshot player, out PluginAccountIdentity? account);
    ValueTask<IReadOnlyList<GroupSnapshot>> SnapshotGroupsAsync(CancellationToken cancellationToken = default);
    ValueTask<GroupSnapshot?> FindGroupAsync(string name, CancellationToken cancellationToken = default);
    ValueTask<bool> GroupHasPermissionAsync(string groupName, string permission, CancellationToken cancellationToken = default);
    /// <summary>Returns false when no exact current authentication exists. The output is observational, not a grant or authorization lease.</summary>
    bool TryHasAuthenticatedPermission(PlayerSnapshot player, string permission, out bool allowed);
}

public interface IPluginWarps
{
    bool IsAvailable { get; }
    /// <summary>Public warps for the hosted persistent world; null when no current world identity exists.</summary>
    ValueTask<WorldWarpCatalogSnapshot?> SnapshotAsync(CancellationToken cancellationToken = default);
}
