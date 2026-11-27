using System.Text.Json.Serialization;

namespace Vega.Contracts.Cluster;

/// <summary>Namespaced, versioned plugin notification. This wire family never carries executable commands or requests.</summary>
public sealed record PluginClusterNotification(string Plugin, string Topic, int Version, byte[] Payload) : IClusterEvent
{
    public const int MaximumPayloadBytes = 16 * 1024;
    [JsonIgnore] public string Schema => "vega.cluster.plugin.notification";
    [JsonIgnore] public int SchemaVersion => 1;

    [JsonIgnore] public bool IsValid => ValidPluginName(Plugin) && ValidName(Topic, 96) && Version > 0 &&
        Payload is { Length: > 0 and <= MaximumPayloadBytes };

    public static bool ValidPluginName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        !value.Any(char.IsControl);

    public static bool ValidName(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value is not ("." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

public sealed record ClusterPeerDelivery(ServerId Peer, ClusterDeliveryResult Delivery);

/// <summary>Explicit configured peer notifications, separate from cluster administration and remote commands.</summary>
public interface IClusterNotificationSender
{
    bool IsAvailable { get; }
    ServerId LocalServerId { get; }
    IReadOnlyList<ServerId> Peers { get; }
    ValueTask<ClusterDeliveryResult> SendAsync(ServerId target, PluginClusterNotification notification, CancellationToken token = default);
    ValueTask<IReadOnlyList<ClusterPeerDelivery>> BroadcastAsync(PluginClusterNotification notification, CancellationToken token = default);
}
