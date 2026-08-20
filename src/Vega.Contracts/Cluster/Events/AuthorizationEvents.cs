using TShockNG.Contracts.Accounts;

namespace Vega.Contracts.Cluster.Events;

public sealed record AccountGroupChanged(
    AccountId AccountId,
    string GroupName,
    long Revision) : IClusterEvent
{
    public string Schema => "vega.cluster.authorization.account-group-changed";

    public int SchemaVersion => 1;
}

public sealed record PermissionAssignmentChanged(
    string GroupName,
    string Permission,
    bool Granted,
    long Revision) : IClusterEvent
{
    public string Schema => "vega.cluster.authorization.permission-changed";

    public int SchemaVersion => 1;
}

public interface IReplicatedAuthorizationState
{
    bool Apply(AccountGroupChanged changed);

    bool Apply(PermissionAssignmentChanged changed);
}
