using Vega.Contracts.Runtime;

namespace Vega.Contracts.Cluster.Events;

public sealed record ServerPresenceAnnounced(
    ServerId ServerId,
    string DisplayName,
    RuntimeLifecycleStage Stage,
    int OnlinePlayers,
    DateTimeOffset ExpiresAt) : IClusterEvent
{
    public string Schema => "vega.cluster.presence.announced";

    public int SchemaVersion => 1;
}

public sealed record ServerPresenceWithdrawn(ServerId ServerId) : IClusterEvent
{
    public string Schema => "vega.cluster.presence.withdrawn";

    public int SchemaVersion => 1;
}
