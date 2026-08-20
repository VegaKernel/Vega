namespace Vega.Contracts.Cluster.Events;

public readonly record struct GlobalBanId(Guid Value)
{
    public static GlobalBanId New() => new(Guid.NewGuid());
}

public enum GlobalBanSubjectKind
{
    PlayerName,
    Account,
    Uuid,
    IpAddress,
    Network
}

public sealed record GlobalBanSubject(GlobalBanSubjectKind Kind, string Value);

public sealed record GlobalBanCreated(
    GlobalBanId BanId,
    GlobalBanSubject Subject,
    string Reason,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long Revision) : IClusterEvent
{
    public string Schema => "vega.cluster.global-ban.created";

    public int SchemaVersion => 1;
}

public sealed record GlobalBanRevoked(
    GlobalBanId BanId,
    string RevokedBy,
    DateTimeOffset RevokedAt,
    long Revision) : IClusterEvent
{
    public string Schema => "vega.cluster.global-ban.revoked";

    public int SchemaVersion => 1;
}

public interface IGlobalBanReplica
{
    ValueTask<bool> ApplyCreatedAsync(GlobalBanCreated created, CancellationToken cancellationToken = default);

    ValueTask<bool> ApplyRevokedAsync(GlobalBanRevoked revoked, CancellationToken cancellationToken = default);

    IReadOnlyList<GlobalBanCreated> SnapshotActive(DateTimeOffset now);
}
