using System.Net;
using Vega.Contracts.Cluster.Events;

namespace Vega.Contracts.Bans;

public interface IBanService
{
    bool TryNormalizeSubject(
        GlobalBanSubject subject,
        out GlobalBanSubject? normalized,
        out string? error);

    ValueTask<bool> CreateAsync(
        GlobalBanCreated created,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RevokeAsync(
        GlobalBanRevoked revoked,
        CancellationToken cancellationToken = default);

    ValueTask<BanSnapshot?> FindActiveBanAsync(
        BanIdentity identity,
        CancellationToken cancellationToken = default);

    IReadOnlyList<BanSnapshot> SnapshotActive(DateTimeOffset now);
}

public sealed record BanIdentity(
    string? PlayerName,
    string? AccountName,
    string? Uuid,
    IPAddress? IpAddress);
