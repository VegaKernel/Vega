using TShockNG.Contracts.Accounts;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;

namespace Vega.Cluster.Scenarios.Authorization;

public sealed class ReplicatedAuthorizationState :
    IReplicatedAuthorizationState,
    IClusterEventHandler<AccountGroupChanged>,
    IClusterEventHandler<PermissionAssignmentChanged>
{
    private readonly object sync = new();
    private readonly Dictionary<AccountId, (long Revision, string Group)> accountGroups = [];
    private readonly Dictionary<(string Group, string Permission), (long Revision, bool Granted)> permissions = [];

    public bool Apply(AccountGroupChanged changed)
    {
        lock (sync)
        {
            if (accountGroups.TryGetValue(changed.AccountId, out var current) && current.Revision >= changed.Revision)
            {
                return false;
            }

            accountGroups[changed.AccountId] = (changed.Revision, changed.GroupName);
            return true;
        }
    }

    public bool Apply(PermissionAssignmentChanged changed)
    {
        lock (sync)
        {
            var key = (changed.GroupName, changed.Permission);
            if (permissions.TryGetValue(key, out var current) && current.Revision >= changed.Revision)
            {
                return false;
            }

            permissions[key] = (changed.Revision, changed.Granted);
            return true;
        }
    }

    public ValueTask HandleAsync(
        ClusterEnvelope<AccountGroupChanged> envelope,
        CancellationToken cancellationToken)
    {
        Apply(envelope.Message);
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(
        ClusterEnvelope<PermissionAssignmentChanged> envelope,
        CancellationToken cancellationToken)
    {
        Apply(envelope.Message);
        return ValueTask.CompletedTask;
    }

    public bool TryGetAccountGroup(AccountId accountId, out string? group)
    {
        lock (sync)
        {
            if (accountGroups.TryGetValue(accountId, out var value))
            {
                group = value.Group;
                return true;
            }

            group = null;
            return false;
        }
    }

    public bool IsGranted(string group, string permission)
    {
        lock (sync)
        {
            return permissions.TryGetValue((group, permission), out var value) && value.Granted;
        }
    }
}

public sealed class AuthorizationReplicationPublisher(
    IClusterPublisher publisher,
    ClusterMessageFactory messages)
{
    public ValueTask<ClusterDeliveryResult> PublishAsync(
        AccountGroupChanged changed,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(messages.Create(changed), cancellationToken);

    public ValueTask<ClusterDeliveryResult> PublishAsync(
        PermissionAssignmentChanged changed,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(messages.Create(changed), cancellationToken);
}
