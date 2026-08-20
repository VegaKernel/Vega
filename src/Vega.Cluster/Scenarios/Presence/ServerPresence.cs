using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Runtime;

namespace Vega.Cluster.Scenarios.Presence;

public sealed record ServerPresenceSnapshot(
    ServerId ServerId,
    string DisplayName,
    RuntimeLifecycleStage Stage,
    int OnlinePlayers,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt);

public sealed class ServerPresenceDirectory(TimeProvider? timeProvider = null) :
    IClusterEventHandler<ServerPresenceAnnounced>,
    IClusterEventHandler<ServerPresenceWithdrawn>
{
    private readonly object sync = new();
    private readonly Dictionary<ServerId, ServerPresenceSnapshot> servers = [];
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public ValueTask HandleAsync(
        ClusterEnvelope<ServerPresenceAnnounced> envelope,
        CancellationToken cancellationToken)
    {
        ServerPresenceAnnounced presence = envelope.Message;
        if (presence.ServerId != envelope.Metadata.SenderServerId)
        {
            throw new InvalidOperationException("Presence server identity does not match the envelope sender.");
        }

        if (presence.ExpiresAt <= time.GetUtcNow())
        {
            return ValueTask.CompletedTask;
        }

        lock (sync)
        {
            if (!servers.TryGetValue(presence.ServerId, out ServerPresenceSnapshot? current) ||
                envelope.Metadata.Timestamp >= current.LastSeenAt)
            {
                servers[presence.ServerId] = new ServerPresenceSnapshot(
                    presence.ServerId,
                    presence.DisplayName,
                    presence.Stage,
                    presence.OnlinePlayers,
                    envelope.Metadata.Timestamp,
                    presence.ExpiresAt);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(
        ClusterEnvelope<ServerPresenceWithdrawn> envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.Message.ServerId != envelope.Metadata.SenderServerId)
        {
            throw new InvalidOperationException("Presence server identity does not match the envelope sender.");
        }

        lock (sync)
        {
            if (servers.TryGetValue(envelope.Message.ServerId, out ServerPresenceSnapshot? current) &&
                envelope.Metadata.Timestamp >= current.LastSeenAt)
            {
                servers.Remove(envelope.Message.ServerId);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ServerPresenceSnapshot> SnapshotOnline()
    {
        lock (sync)
        {
            DateTimeOffset now = time.GetUtcNow();
            foreach (ServerId expired in servers
                .Where(pair => pair.Value.ExpiresAt <= now)
                .Select(static pair => pair.Key)
                .ToArray())
            {
                servers.Remove(expired);
            }

            return Array.AsReadOnly(servers.Values.OrderBy(static server => server.ServerId.Value).ToArray());
        }
    }
}

public sealed class ServerPresencePublisher(
    ServerId serverId,
    string displayName,
    IClusterPublisher publisher,
    ClusterMessageFactory messages,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public ValueTask<ClusterDeliveryResult> AnnounceAsync(
        RuntimeLifecycleStage stage,
        int onlinePlayers,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var presence = new ServerPresenceAnnounced(
            serverId,
            displayName,
            stage,
            onlinePlayers,
            time.GetUtcNow() + lifetime);
        return publisher.PublishAsync(messages.Create(presence), cancellationToken);
    }

    public ValueTask<ClusterDeliveryResult> WithdrawAsync(CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            messages.Create(new ServerPresenceWithdrawn(serverId)),
            cancellationToken);
}
