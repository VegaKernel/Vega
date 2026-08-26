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
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(subject);
        normalized = null;
        error = null;

        if (subject.Kind == GlobalBanSubjectKind.Uuid)
        {
            return TryNormalizeUuidSubject(subject, out normalized, out error);
        }

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
            case GlobalBanSubjectKind.IpAddress:
                if (!TryParseIpv4(value, out IPAddress? address))
                {
                    error = "The IP ban subject is not a valid IPv4 address.";
                    return false;
                }

                normalized = subject with { Value = address!.ToString() };
                return true;
            case GlobalBanSubjectKind.Network:
                if (!TryParseNetwork(value, out IPAddress? network, out int prefixLength))
                {
                    error = "The network ban subject is not valid IPv4 CIDR notation.";
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
        BanSnapshot? match = replica.SnapshotActive(now)
            .Select(created => (Created: created, Specificity: MatchSpecificity(created.Subject, identity)))
            .Where(static candidate => candidate.Specificity >= 0)
            .OrderByDescending(static candidate => candidate.Specificity)
            .ThenByDescending(static candidate => candidate.Created.Revision)
            .ThenByDescending(static candidate => candidate.Created.CreatedAt)
            .Select(static candidate => ToSnapshot(candidate.Created))
            .FirstOrDefault();
        return ValueTask.FromResult(match);
    }

    public IReadOnlyList<BanSnapshot> SnapshotActive(DateTimeOffset now) =>
        Array.AsReadOnly(replica.SnapshotActive(now).Select(ToSnapshot).ToArray());

    private int MatchSpecificity(GlobalBanSubject rawSubject, BanIdentity identity)
    {
        if (!TryNormalizeSubject(rawSubject, out GlobalBanSubject? subject, out _))
        {
            return -1;
        }

        IPAddress? identityAddress = identity.IpAddress is { AddressFamily: AddressFamily.InterNetwork }
            ? identity.IpAddress
            : null;

        return subject!.Kind switch
        {
            GlobalBanSubjectKind.Account when EqualsIgnoreCase(subject.Value, identity.AccountName) => 600,
            GlobalBanSubjectKind.Uuid when TryNormalizeUuid(identity.Uuid, out string? uuid) &&
                string.Equals(subject.Value, uuid, StringComparison.Ordinal) => 600,
            GlobalBanSubjectKind.PlayerName when EqualsIgnoreCase(subject.Value, identity.PlayerName) => 500,
            GlobalBanSubjectKind.IpAddress when identityAddress is not null &&
                IPAddress.Parse(subject.Value).Equals(identityAddress) => 400,
            GlobalBanSubjectKind.Network when identityAddress is not null &&
                TryParseNetwork(subject.Value, out IPAddress? network, out int prefixLength) &&
                Contains(network!, prefixLength, identityAddress) => 100 + prefixLength,
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

    private static bool TryNormalizeUuidSubject(
        GlobalBanSubject subject,
        out GlobalBanSubject? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (!TryNormalizeUuid(subject.Value, out string? value))
        {
            error = $"The UUID ban subject must be a non-whitespace Terraria ClientUUID up to {ConnectionMetadataLimits.MaxClientUuidLength} characters.";
            return false;
        }

        normalized = subject with { Value = value! };
        return true;
    }

    private static bool TryNormalizeUuid(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > ConnectionMetadataLimits.MaxClientUuidLength)
        {
            return false;
        }

        // Terraria persists ClientUUID as an opaque token. Canonicalize actual GUID values so
        // historical GUID-form bans remain format-insensitive, but preserve every non-GUID token exactly.
        normalized = Guid.TryParse(value, out Guid uuid)
            ? uuid.ToString("D")
            : value;
        return true;
    }

    private static bool TryParseIpv4(string value, out IPAddress? address)
    {
        if (IPAddress.TryParse(value, out address) &&
            address.AddressFamily == AddressFamily.InterNetwork)
        {
            return true;
        }

        address = null;
        return false;
    }

    private static bool TryParseNetwork(
        string value,
        out IPAddress? network,
        out int prefixLength)
    {
        network = null;
        prefixLength = 0;
        string[] parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !TryParseIpv4(parts[0], out IPAddress? parsed) ||
            !int.TryParse(parts[1], out prefixLength) ||
            prefixLength < 0 || prefixLength > 32)
        {
            return false;
        }

        byte[] bytes = parsed!.GetAddressBytes();
        ApplyMask(bytes, prefixLength);
        network = new IPAddress(bytes);
        return true;
    }

    private static bool Contains(IPAddress network, int prefixLength, IPAddress address)
    {
        if (network.AddressFamily != AddressFamily.InterNetwork ||
            address.AddressFamily != AddressFamily.InterNetwork)
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
