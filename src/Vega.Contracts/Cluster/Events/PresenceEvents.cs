using Vega.Contracts.Runtime;

namespace Vega.Contracts.Cluster.Events;

public sealed record ServerPresenceAnnounced(
    ServerId ServerId,
    string DisplayName,
    RuntimeLifecycleStage Stage,
    int OnlinePlayers,
    DateTimeOffset ExpiresAt) : IClusterEvent
{
    public ClusterServerRole Role { get; init; }
    public int MaximumPlayers { get; init; }
    public bool IsWorldReady { get; init; }

    public string Schema => "vega.cluster.presence.announced";

    public int SchemaVersion => 1;
}

public sealed record ServerPresenceWithdrawn(ServerId ServerId) : IClusterEvent
{
    public string Schema => "vega.cluster.presence.withdrawn";

    public int SchemaVersion => 1;
}
