using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Vega.Contracts.Cluster;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    public IPluginCluster Cluster { get; private set; } = null!;
    private void InitializePluginCluster(string pluginName, PluginClusterRegistry? registry, IClusterNotificationSender? sender) =>
        Cluster = new PluginCluster(this, pluginName, registry, sender);

    private sealed class PluginCluster(HotModuleRegistrationScope owner, string pluginName, PluginClusterRegistry? registry,
        IClusterNotificationSender? sender) : IPluginCluster
    {
        public bool IsAvailable => registry is not null && sender?.IsAvailable == true;
        public ServerId? LocalServerId => sender?.LocalServerId;
        public IReadOnlyList<ServerId> Peers => sender?.Peers ?? [];
        public IReadOnlyList<ClusterServerStatus> SnapshotServers() => owner.executionGuard.AcceptsCallbacks && sender is IClusterServerDirectory directory
            ? directory.SnapshotServers() : [];
        public ClusterServerStatus? SelectRoute(ClusterServerRole role) => owner.executionGuard.AcceptsCallbacks && sender is IClusterServerDirectory directory
            ? directory.SelectRoute(role) : null;

        public void Subscribe<T>(string topic, int version, JsonTypeInfo<T> payloadType,
            Func<PluginClusterMessage<T>, CancellationToken, ValueTask> handler)
        {
            ArgumentNullException.ThrowIfNull(payloadType);
            ArgumentNullException.ThrowIfNull(handler);
            if (registry is null) throw new NotSupportedException("Cluster transport is unavailable in this host.");
            owner.RegisterOwned(() => registry.Register(pluginName, topic, version, () => owner.executionGuard.AcceptsCallbacks, Invoke));

            async ValueTask<bool> Invoke(ClusterEnvelope<PluginClusterNotification> envelope, CancellationToken callerToken)
            {
                try
                {
                    return await owner.RunScopedAsync(async token =>
                    {
                        T? payload;
                        try { payload = JsonSerializer.Deserialize(envelope.Message.Payload, payloadType); }
                        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return false; }
                        if (payload is null) return false;
                        var message = new PluginClusterMessage<T>(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId, envelope.Metadata.Timestamp, payload);
                        var result = await owner.executionGuard.InvokeServiceAsync($"cluster:{topic}:v{version}", async ct =>
                        {
                            await handler(message, ct).ConfigureAwait(false);
                            return true;
                        }, token).ConfigureAwait(false);
                        return result.Accepted;
                    }, callerToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or PluginScopeBusyException) { return false; }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested) { return false; }
            }
        }

        public ValueTask<ClusterDeliveryResult> SendAsync<T>(ServerId peer, string topic, int version, T payload,
            JsonTypeInfo<T> payloadType, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(payloadType);
            PluginClusterRegistry.Key(pluginName, topic, version);
            return owner.RunScopedAsync(token => sender?.IsAvailable == true
                ? sender.SendAsync(peer, Serialize(topic, version, payload, payloadType), token)
                : ValueTask.FromResult(ClusterDeliveryResult.Unavailable("Cluster transport is unavailable.")), cancellationToken);
        }

        public ValueTask<IReadOnlyList<ClusterPeerDelivery>> BroadcastAsync<T>(string topic, int version, T payload,
            JsonTypeInfo<T> payloadType, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(payloadType);
            PluginClusterRegistry.Key(pluginName, topic, version);
            return owner.RunScopedAsync(token => sender?.IsAvailable == true
                ? sender.BroadcastAsync(Serialize(topic, version, payload, payloadType), token)
                : ValueTask.FromResult<IReadOnlyList<ClusterPeerDelivery>>(Array.AsReadOnly(Peers.Select(peer => new ClusterPeerDelivery(peer,
                    ClusterDeliveryResult.Unavailable("Cluster transport is unavailable."))).ToArray())), cancellationToken);
        }

        private PluginClusterNotification Serialize<T>(string topic, int version, T payload, JsonTypeInfo<T> payloadType)
        {
            ArgumentNullException.ThrowIfNull(payload);
            // Reject oversized output before admission to the peer transport. JsonTypeInfo belongs to the owning plugin.
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, payloadType);
            if (json.Length > PluginClusterNotification.MaximumPayloadBytes) throw new ArgumentException("Cluster notification exceeds 16 KiB.", nameof(payload));
            return new(pluginName, topic, version, json);
        }
    }
}
