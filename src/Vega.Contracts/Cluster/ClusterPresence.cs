using System.Text.Json.Serialization;
using Vega.Contracts.Runtime;

namespace Vega.Contracts.Cluster;

/// <summary>Operator-declared routing role. Unspecified nodes can report presence but are not route candidates.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClusterServerRole>))]
public enum ClusterServerRole { Unspecified, Lobby, Game }

/// <summary>A detached observation of an authenticated peer, not an admission or capacity reservation.</summary>
public sealed record ClusterServerStatus(ServerId ServerId, string DisplayName, ClusterServerRole Role,
    RuntimeLifecycleStage Stage, int OnlinePlayers, int MaximumPlayers, bool IsWorldReady,
    DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt)
{
    public bool IsRouteCandidate => Role != ClusterServerRole.Unspecified && IsWorldReady &&
        Stage is >= RuntimeLifecycleStage.NetworkReady and <= RuntimeLifecycleStage.FullyWarmed &&
        MaximumPlayers > OnlinePlayers;
}

/// <summary>Read-only peer presence and advisory routing (never an authorization or transfer action).</summary>
public interface IClusterServerDirectory
{
    IReadOnlyList<ClusterServerStatus> SnapshotServers();
    ClusterServerStatus? SelectRoute(ClusterServerRole role);
}

/// <summary>Host-owned aggregate state sampled without accessing mutable native Terraria objects.</summary>
public readonly record struct ClusterPresenceSample(RuntimeLifecycleStage Stage, int OnlinePlayers, int MaximumPlayers, bool IsWorldReady);
