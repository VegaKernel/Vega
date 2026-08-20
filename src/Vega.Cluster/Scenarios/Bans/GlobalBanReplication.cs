using Vega.Contracts.Players;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;

namespace Vega.Cluster.Scenarios.Bans;

public sealed class InMemoryGlobalBanReplica : IGlobalBanReplica
{
    private readonly object sync = new();
    private readonly Dictionary<GlobalBanId, Entry> entries = [];

    public ValueTask<bool> ApplyCreatedAsync(
        GlobalBanCreated created,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (entries.TryGetValue(created.BanId, out Entry? current) && current.Revision >= created.Revision)
            {
                return ValueTask.FromResult(false);
            }

            entries[created.BanId] = new Entry(created.Revision, created, false);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> ApplyRevokedAsync(
        GlobalBanRevoked revoked,
        CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (entries.TryGetValue(revoked.BanId, out Entry? current) && current.Revision >= revoked.Revision)
            {
                return ValueTask.FromResult(false);
            }

            entries[revoked.BanId] = new Entry(revoked.Revision, current?.Created, true);
            return ValueTask.FromResult(true);
        }
    }

    public IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now)
    {
        lock (sync)
        {
            return Array.AsReadOnly(entries.Values
                .Where(entry => !entry.Revoked &&
                    entry.Created is not null &&
                    (entry.Created.ExpiresAt is null || entry.Created.ExpiresAt > now))
                .Select(static entry => entry.Created!)
                .ToArray());
        }
    }

    private sealed record Entry(long Revision, GlobalBanCreated? Created, bool Revoked);
}

public readonly record struct GlobalBanMutationResult(
    bool LocalStateChanged,
    ClusterDeliveryResult Publication);

public sealed class GlobalBanCoordinator(
    IGlobalBanReplica localReplica,
    IClusterPublisher publisher,
    ClusterMessageFactory messages,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async ValueTask<GlobalBanMutationResult> CreateAsync(
        GlobalBanSubject subject,
        string reason,
        string createdBy,
        DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var created = new GlobalBanCreated(
            GlobalBanId.New(),
            subject,
            reason,
            createdBy,
            time.GetUtcNow(),
            expiresAt,
            1);
		bool changed = await localReplica.ApplyCreatedAsync(created, cancellationToken).ConfigureAwait(false);
		if (!changed)
		{
			return new GlobalBanMutationResult(
				false,
				new ClusterDeliveryResult(
					ClusterDeliveryStatus.Rejected,
					"The local ban replica rejected the duplicate creation."));
		}

		ClusterDeliveryResult publication = await publisher
            .PublishAsync(messages.Create(created), cancellationToken)
            .ConfigureAwait(false);
        return new GlobalBanMutationResult(changed, publication);
    }

    public async ValueTask<GlobalBanMutationResult> RevokeAsync(
        GlobalBanId banId,
        string revokedBy,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var revoked = new GlobalBanRevoked(banId, revokedBy, time.GetUtcNow(), revision);
        bool changed = await localReplica.ApplyRevokedAsync(revoked, cancellationToken).ConfigureAwait(false);
        if (!changed)
        {
            return new GlobalBanMutationResult(
                false,
                new ClusterDeliveryResult(
                    ClusterDeliveryStatus.Rejected,
                    "The local ban replica rejected the stale or duplicate revocation."));
        }

        ClusterDeliveryResult publication = await publisher
            .PublishAsync(messages.Create(revoked), cancellationToken)
            .ConfigureAwait(false);
        return new GlobalBanMutationResult(changed, publication);
    }
}

public sealed class GlobalBanCreatedHandler(
    IGlobalBanReplica localReplica,
    IPlayerDirectory players,
    IPlayerOperations playerOperations,
    TimeProvider? timeProvider = null) : IClusterEventHandler<GlobalBanCreated>
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async ValueTask HandleAsync(
        ClusterEnvelope<GlobalBanCreated> envelope,
        CancellationToken cancellationToken)
    {
        GlobalBanCreated created = envelope.Message;
        if (!await localReplica.ApplyCreatedAsync(created, cancellationToken).ConfigureAwait(false) ||
            created.ExpiresAt <= time.GetUtcNow() ||
            created.Subject.Kind != GlobalBanSubjectKind.PlayerName)
        {
            return;
        }

        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            if (string.Equals(player.Name, created.Subject.Value, StringComparison.OrdinalIgnoreCase))
            {
                await playerOperations
                    .KickAsync(player.Id, $"Globally banned: {created.Reason}", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

public sealed class GlobalBanRevokedHandler(IGlobalBanReplica localReplica) :
    IClusterEventHandler<GlobalBanRevoked>
{
    public async ValueTask HandleAsync(
        ClusterEnvelope<GlobalBanRevoked> envelope,
        CancellationToken cancellationToken) =>
        await localReplica.ApplyRevokedAsync(envelope.Message, cancellationToken).ConfigureAwait(false);
}
