using Vega.Contracts.Cluster.Events;

namespace Vega.Contracts.Bans;

public sealed record BanSnapshot(
    GlobalBanId Id,
    GlobalBanSubject Subject,
    string Reason,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long Revision);
