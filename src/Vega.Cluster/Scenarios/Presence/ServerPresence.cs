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
    DateTimeOffset ExpiresAt)
{
    public ClusterServerRole Role { get; init; }
    public int MaximumPlayers { get; init; }
    public bool IsWorldReady { get; init; }
}

public sealed class ServerPresenceDirectory(TimeProvider? timeProvider = null,
    IReadOnlyDictionary<ServerId, ClusterServerRole>? configuredPeers = null) :
    IClusterEventHandler<ServerPresenceAnnounced>,
    IClusterEventHandler<ServerPresenceWithdrawn>, IClusterServerDirectory
{
    private const int MaximumTrackedServers = 128;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(1);
    private sealed record Entry(DateTimeOffset LastTimestamp, ServerPresenceSnapshot? Snapshot,
        long ReceivedAt = 0, TimeSpan Lifetime = default);
    private readonly object sync = new();
    private readonly Dictionary<ServerId, Entry> servers = [];
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<ServerId, ClusterServerRole>? roles = configuredPeers is null
        ? null : new Dictionary<ServerId, ClusterServerRole>(configuredPeers);

    public ValueTask HandleAsync(
        ClusterEnvelope<ServerPresenceAnnounced> envelope,
        CancellationToken cancellationToken)
    {
        ServerPresenceAnnounced presence = envelope.Message;
        ValidateServer(presence.ServerId, envelope.Metadata);
        if (string.IsNullOrWhiteSpace(presence.DisplayName) || presence.DisplayName.Length > 128 || presence.DisplayName.Any(char.IsControl) ||
            !Enum.IsDefined(presence.Stage) || !Enum.IsDefined(presence.Role) || presence.OnlinePlayers is < 0 or > 255 ||
            presence.MaximumPlayers is < 0 or > 255)
            throw new ArgumentException("Invalid server presence values.");

        DateTimeOffset now = time.GetUtcNow();
        TimeSpan lifetime = presence.ExpiresAt - envelope.Metadata.Timestamp;
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumLifetime)
            throw new ArgumentException("Presence lifetime lies outside its allowed window.");
        if (presence.ExpiresAt <= now) return ValueTask.CompletedTask;
        // A future-skewed sender cannot extend presence beyond one receive-relative lease.
        DateTimeOffset expires = presence.ExpiresAt - now <= lifetime ? presence.ExpiresAt : now + lifetime;
        long receivedAt = time.GetTimestamp();
        ClusterServerRole role = roles is not null && roles.TryGetValue(presence.ServerId, out var configured) && configured == presence.Role
            ? configured : ClusterServerRole.Unspecified;

        lock (sync)
        {
            servers.TryGetValue(presence.ServerId, out Entry? current);
            if (current is null) EnsureCapacity();
            // Preserve withdrawal/expiry watermarks so delayed messages cannot resurrect a node.
            if (current is null || envelope.Metadata.Timestamp > current.LastTimestamp ||
                (envelope.Metadata.Timestamp == current.LastTimestamp && current.Snapshot is not null && !IsExpired(current, now)))
            {
                var snapshot = new ServerPresenceSnapshot(
                    presence.ServerId,
                    presence.DisplayName,
                    presence.Stage,
                    presence.OnlinePlayers,
                    envelope.Metadata.Timestamp,
                    expires)
                {
                    Role = role,
                    MaximumPlayers = presence.MaximumPlayers,
                    IsWorldReady = presence.IsWorldReady
                };
                servers[presence.ServerId] = new(envelope.Metadata.Timestamp, snapshot, receivedAt, expires - now);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync(
        ClusterEnvelope<ServerPresenceWithdrawn> envelope,
        CancellationToken cancellationToken)
    {
        ValidateServer(envelope.Message.ServerId, envelope.Metadata);

        lock (sync)
        {
            servers.TryGetValue(envelope.Message.ServerId, out Entry? current);
            if (current is null) EnsureCapacity();
            if (current is null || envelope.Metadata.Timestamp >= current.LastTimestamp)
            {
                servers[envelope.Message.ServerId] = new(envelope.Metadata.Timestamp, null);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<ServerPresenceSnapshot> SnapshotOnline()
    {
        lock (sync)
        {
            DateTimeOffset now = time.GetUtcNow();
            // ponytail: this scan/sort is bounded by the small fixed peer directory (128 max).
            foreach (ServerId expired in servers.Where(pair => pair.Value.Snapshot is not null && IsExpired(pair.Value, now)).Select(static pair => pair.Key).ToArray())
                servers[expired] = servers[expired] with { Snapshot = null };
            return Array.AsReadOnly(servers.Values.Select(entry => entry.Snapshot).OfType<ServerPresenceSnapshot>()
                .OrderBy(static server => server.ServerId.Value, StringComparer.Ordinal).ToArray());
        }
    }

    public IReadOnlyList<ClusterServerStatus> SnapshotServers() => Array.AsReadOnly(SnapshotOnline().Select(s => new ClusterServerStatus(
        s.ServerId, s.DisplayName, s.Role, s.Stage, s.OnlinePlayers, s.MaximumPlayers, s.IsWorldReady, s.LastSeenAt, s.ExpiresAt)).ToArray());

    public ClusterServerStatus? SelectRoute(ClusterServerRole role) => SnapshotServers()
        .Where(s => s.Role == role && s.IsRouteCandidate)
        .OrderBy(s => (double)s.OnlinePlayers / s.MaximumPlayers)
        .ThenBy(s => s.OnlinePlayers)
        .ThenBy(s => s.ServerId.Value, StringComparer.Ordinal).FirstOrDefault();

    private void ValidateServer(ServerId id, ClusterMessageMetadata metadata)
    {
        if (id != metadata.SenderServerId || !PluginClusterNotification.ValidName(id.Value, 96) ||
            (roles is not null && !roles.ContainsKey(id)))
            throw new InvalidOperationException("Presence sender is not an allowed server identity.");
        if ((time.GetUtcNow() - metadata.Timestamp).Duration() > MaximumLifetime)
            throw new ArgumentException("Presence timestamp lies outside its allowed window.");
    }

    private void EnsureCapacity()
    {
        if (servers.Count < MaximumTrackedServers) return;
        // The unconfigured in-memory scenario can eventually see new IDs. Forget only old watermarks
        // whose announcements can no longer pass the timestamp gate; configured peers are already bounded.
        DateTimeOffset now = time.GetUtcNow();
        foreach (ServerId stale in servers.Where(pair => now - pair.Value.LastTimestamp > MaximumLifetime &&
                    (pair.Value.Snapshot is null || IsExpired(pair.Value, now))).Select(pair => pair.Key).ToArray())
            servers.Remove(stale);
        if (servers.Count >= MaximumTrackedServers) throw new InvalidOperationException("Presence directory capacity reached.");
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) => entry.Snapshot!.ExpiresAt <= now ||
        time.GetElapsedTime(entry.ReceivedAt) >= entry.Lifetime;
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
