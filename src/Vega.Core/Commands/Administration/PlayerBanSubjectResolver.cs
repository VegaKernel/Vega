using System.Net;
using System.Net.Sockets;
using Vega.Contracts.Accounts;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

internal static class PlayerBanSubjectResolver
{
    public static IReadOnlyList<GlobalBanSubject> Resolve(
        PlayerSnapshot player,
        IPlayerAccountSessionService? accountSessions)
    {
        ArgumentNullException.ThrowIfNull(player);

        var subjects = new List<GlobalBanSubject>(3);
        if (accountSessions is not null &&
            accountSessions.TryGetAccount(player.Id, out AccountSnapshot? account) &&
            account is not null &&
            !string.IsNullOrWhiteSpace(account.Name))
        {
            subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.Account, account.Name));
        }

        string? clientUuid = player.Connection?.ClientUuid;
        if (!string.IsNullOrWhiteSpace(clientUuid))
        {
            subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.Uuid, clientUuid));
        }

        if (TryGetIpAddress(
                player.Connection?.EffectiveEndpoint ?? player.RemoteEndpoint,
                out string? ipAddress) &&
            ipAddress is not null)
        {
            subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, ipAddress));
        }

        if (subjects.Count == 0)
        {
            subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, player.Name));
        }

        return subjects;
    }

    public static IReadOnlyList<GlobalBanSubject> Resolve(
        AccountSnapshot account,
        IAccountIdentityEvidenceService? identityEvidence)
    {
        ArgumentNullException.ThrowIfNull(account);
        var subjects = new List<GlobalBanSubject>
        {
            new(GlobalBanSubjectKind.Account, account.Name)
        };
        if (identityEvidence is null)
        {
            return subjects;
        }

        foreach (AccountIdentityEvidenceSnapshot evidence in identityEvidence.SnapshotEvidence()
            .Where(candidate => candidate.AccountId == account.Id)
            .GroupBy(static candidate => candidate.Kind)
            .Select(static group => group.OrderByDescending(candidate => candidate.LastSeenAt).First()))
        {
            if (evidence.Kind == AccountIdentityEvidenceKind.ClientUuid)
            {
                subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.Uuid, evidence.Value));
            }
            else if (TryGetIpAddress(evidence.Value, out string? ipv4) && ipv4 is not null)
            {
                subjects.Add(new GlobalBanSubject(GlobalBanSubjectKind.IpAddress, ipv4));
            }
        }

        return subjects;
    }

    internal static bool TryGetIpAddress(string? remoteEndpoint, out string? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            return false;
        }

        if (IPAddress.TryParse(remoteEndpoint, out IPAddress? direct) &&
            direct.AddressFamily == AddressFamily.InterNetwork)
        {
            address = direct.ToString();
            return true;
        }

        if (IPEndPoint.TryParse(remoteEndpoint, out IPEndPoint? endpoint) &&
            endpoint.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            address = endpoint.Address.ToString();
            return true;
        }

        return false;
    }
}
