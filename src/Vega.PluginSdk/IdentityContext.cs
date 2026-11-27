using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Minimal account directory identity, never password material or private profile metadata.</summary>
public sealed record PluginAccountIdentity(AccountId Id, string Name, string GroupName);

/// <summary>Read-only host identity and authorization queries; no mutation or permission grants.</summary>
public interface IPluginIdentity
{
    bool AccountsAvailable { get; }
    bool GroupsAvailable { get; }
    bool AuthenticatedPermissionsAvailable { get; }

    ValueTask<PluginAccountIdentity?> FindAccountAsync(string name, CancellationToken cancellationToken = default);
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
