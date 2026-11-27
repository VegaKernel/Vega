using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Cluster;

namespace Vega.PluginSdk;

/// <summary>Detached, typed payload and authenticated source server for the current plugin's notification topic.</summary>
public sealed record PluginClusterMessage<T>(ServerId Sender, ClusterMessageId MessageId, DateTimeOffset SentAt, T Payload);

/// <summary>Optional peer messaging. Only the same plugin namespace and explicitly registered topic/version receive data.</summary>
public interface IPluginCluster
{
    bool IsAvailable { get; }
    ServerId? LocalServerId { get; }
    /// <summary>Configured peer IDs, not a liveness or presence guarantee.</summary>
    IReadOnlyList<ServerId> Peers { get; }

    /// <summary>Fresh authenticated peer observations only; offline/expired peers are absent.</summary>
    IReadOnlyList<ClusterServerStatus> SnapshotServers() => [];

    /// <summary>Suggest a ready configured peer of the specified role, based on its observed free capacity.
    /// This does not admit, reserve, disconnect or transfer a player.</summary>
    ClusterServerStatus? SelectRoute(ClusterServerRole role) => null;

    /// <summary>Receives on the plugin's tracked background scope, not on the game thread. The subscription retires with the plugin.</summary>
    void Subscribe<T>(string topic, int version, JsonTypeInfo<T> payloadType,
        Func<PluginClusterMessage<T>, CancellationToken, ValueTask> handler);

    ValueTask<ClusterDeliveryResult> SendAsync<T>(ServerId peer, string topic, int version, T payload,
        JsonTypeInfo<T> payloadType, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ClusterPeerDelivery>> BroadcastAsync<T>(string topic, int version, T payload,
        JsonTypeInfo<T> payloadType, CancellationToken cancellationToken = default);
}
