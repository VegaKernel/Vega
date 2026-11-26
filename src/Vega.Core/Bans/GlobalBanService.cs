using System.Net;
using System.Net.Sockets;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Networking;

namespace Vega.Core.Bans;

public sealed class GlobalBanService(
    IGlobalBanReplica replica,
    TimeProvider? timeProvider = null) : IBanService
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public bool TryNormalizeSubject(
        GlobalBanSubject subject,
        out GlobalBanSubject? normalized,
        out string? error) => TryNormalizeSubjectCore(subject, out normalized, out error);

    private static bool TryNormalizeSubjectCore(
        GlobalBanSubject subject,
        out GlobalBanSubject? normalized,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(subject);
        normalized = null;
        error = null;
        string value = subject.Value.Trim();
        if (value.Length == 0)
        {
            error = "The ban subject is empty.";
            return false;
        }

        switch (subject.Kind)
        {
            case GlobalBanSubjectKind.PlayerName:
            case GlobalBanSubjectKind.Account:
                normalized = subject with { Value = value };
                return true;
            case GlobalBanSubjectKind.Uuid:
                if (!TryNormalizeUuid(subject.Value, out string? uuid))
                {
                    error = "The UUID ban subject must be a non-empty Terraria client token of at most 128 characters.";
                    return false;
                }

                normalized = subject with { Value = uuid! };
                return true;
            case GlobalBanSubjectKind.IpAddress:
                if (!IPAddress.TryParse(value, out IPAddress? address))
                {
                    error = "The IP ban subject is not a valid IPv4 or IPv6 address.";
                    return false;
                }

                normalized = subject with { Value = NormalizeAddress(address).ToString() };
                return true;
            case GlobalBanSubjectKind.Network:
                if (!TryParseNetwork(value, out IPAddress? network, out int prefixLength))
                {
                    error = "The network ban subject is not valid CIDR notation.";
                    return false;
                }

                normalized = subject with { Value = $"{network}/{prefixLength}" };
                return true;
            default:
                error = $"Unsupported ban subject kind '{subject.Kind}'.";
                return false;
        }
    }

    public async ValueTask<bool> CreateAsync(
        GlobalBanCreated created,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeSubject(created.Subject, out GlobalBanSubject? subject, out string? error))
        {
            throw new ArgumentException(error, nameof(created));
        }

        return await replica.ApplyCreatedAsync(
            created with { Subject = subject! },
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> RevokeAsync(
        GlobalBanRevoked revoked,
        CancellationToken cancellationToken = default) =>
        replica.ApplyRevokedAsync(revoked, cancellationToken);

    public ValueTask<BanSnapshot?> FindActiveBanAsync(
        BanIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = time.GetUtcNow();
        return ValueTask.FromResult(MatchActive(SnapshotActive(now), identity, now));
    }

    public IReadOnlyList<BanSnapshot> SnapshotActive(DateTimeOffset now) =>
        Array.AsReadOnly(replica.SnapshotActive(now).Select(ToSnapshot).ToArray());

    /// <summary>Match an already-read active-ban snapshot without accessing persistence.</summary>
    internal static BanSnapshot? MatchActive(IEnumerable<BanSnapshot> active, BanIdentity identity, DateTimeOffset now)
    {
        BanSnapshot? best = null;
        int bestSpecificity = -1;
        foreach (BanSnapshot candidate in active)
        {
            if (candidate.ExpiresAt is { } expires && expires <= now) continue;
            int specificity = MatchSpecificity(candidate.Subject, identity);
            if (specificity < 0) continue;
            if (best is null || specificity > bestSpecificity || specificity == bestSpecificity &&
                (candidate.Revision > best.Revision || candidate.Revision == best.Revision && candidate.CreatedAt > best.CreatedAt))
            {
                best = candidate;
                bestSpecificity = specificity;
            }
        }
        return best;
    }

    private static int MatchSpecificity(GlobalBanSubject rawSubject, BanIdentity identity)
    {
        if (!TryNormalizeSubjectCore(rawSubject, out GlobalBanSubject? subject, out _))
        {
            return -1;
        }

        return subject!.Kind switch
        {
            GlobalBanSubjectKind.Account when EqualsIgnoreCase(subject.Value, identity.AccountName) => 600,
            GlobalBanSubjectKind.Uuid when TryNormalizeUuid(identity.Uuid, out string? uuid) &&
                string.Equals(subject.Value, uuid, StringComparison.Ordinal) => 600,
            GlobalBanSubjectKind.PlayerName when EqualsIgnoreCase(subject.Value, identity.PlayerName) => 500,
            GlobalBanSubjectKind.IpAddress when identity.IpAddress is not null &&
                IPAddress.Parse(subject.Value).Equals(NormalizeAddress(identity.IpAddress)) => 400,
            GlobalBanSubjectKind.Network when identity.IpAddress is not null &&
                TryParseNetwork(subject.Value, out IPAddress? network, out int prefixLength) &&
                Contains(network!, prefixLength, NormalizeAddress(identity.IpAddress)) => 100 + prefixLength,
            _ => -1
        };
    }

    private static BanSnapshot ToSnapshot(GlobalBanCreated created) =>
        new(
            created.BanId,
            created.Subject,
            created.Reason,
            created.CreatedBy,
            created.CreatedAt,
            created.ExpiresAt,
            created.Revision);

    private static bool EqualsIgnoreCase(string left, string? right) =>
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeUuid(string? value, out string? normalized)
    {
        if (!ClientUuidToken.IsValid(value)) { normalized = null; return false; }
        // Preserve compatibility with existing GUID bans, but keep opaque client tokens exact.
        normalized = value!.Length == 36 && Guid.TryParseExact(value, "D", out Guid uuid) ? uuid.ToString("D") : value;
        return normalized is not null;
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseNetwork(
        string value,
        out IPAddress? network,
        out int prefixLength)
    {
        network = null;
        prefixLength = 0;
        string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out IPAddress? parsed) ||
            !int.TryParse(parts[1], out prefixLength))
        {
            return false;
        }

        parsed = NormalizeAddress(parsed);
        int bitCount = parsed.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > bitCount)
        {
            return false;
        }

        byte[] bytes = parsed.GetAddressBytes();
        ApplyMask(bytes, prefixLength);
        network = new IPAddress(bytes);
        return true;
    }

    private static bool Contains(IPAddress network, int prefixLength, IPAddress address)
    {
        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        byte[] candidate = address.GetAddressBytes();
        ApplyMask(candidate, prefixLength);
        return candidate.AsSpan().SequenceEqual(network.GetAddressBytes());
    }

    private static void ApplyMask(byte[] bytes, int prefixLength)
    {
        int completeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            bytes[completeBytes] &= (byte)(0xff << (8 - remainingBits));
            completeBytes++;
        }

        Array.Clear(bytes, completeBytes, bytes.Length - completeBytes);
    }
}
