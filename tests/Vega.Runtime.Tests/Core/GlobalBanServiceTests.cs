using System.Net;
using NUnit.Framework;
using Vega.Cluster.Scenarios.Bans;
using Vega.Contracts.Bans;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Networking;
using Vega.Core.Bans;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class GlobalBanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task NormalizesIpv4MappedAddressAndMatchesExactIp()
    {
        var replica = new InMemoryGlobalBanReplica();
        var service = CreateService(replica);
        GlobalBanCreated created = Ban(GlobalBanSubjectKind.IpAddress, "::ffff:192.0.2.42");

        Assert.That(await service.CreateAsync(created), Is.True);
        BanSnapshot? match = await service.FindActiveBanAsync(
            new BanIdentity(null, null, null, IPAddress.Parse("192.0.2.42")));

        Assert.Multiple(() =>
        {
            Assert.That(match?.Id, Is.EqualTo(created.BanId));
            Assert.That(match?.Subject.Value, Is.EqualTo("192.0.2.42"));
        });
    }

    [Test]
    public async Task ChoosesMostSpecificOverlappingIpv6Network()
    {
        var replica = new InMemoryGlobalBanReplica();
        var service = CreateService(replica);
        GlobalBanCreated broad = Ban(GlobalBanSubjectKind.Network, "2001:db8::1234/48");
        GlobalBanCreated narrow = Ban(GlobalBanSubjectKind.Network, "2001:db8:0:1::99/64");
        await service.CreateAsync(broad);
        await service.CreateAsync(narrow);

        BanSnapshot? match = await service.FindActiveBanAsync(
            new BanIdentity(null, null, null, IPAddress.Parse("2001:db8:0:1::7")));

        Assert.Multiple(() =>
        {
            Assert.That(match?.Id, Is.EqualTo(narrow.BanId));
            Assert.That(service.SnapshotActive(Now).Single(ban => ban.Id == broad.BanId).Subject.Value,
                Is.EqualTo("2001:db8::/48"));
        });
    }

    [TestCase(GlobalBanSubjectKind.Account, "Alice", "alice")]
    [TestCase(GlobalBanSubjectKind.PlayerName, "Alice", "ALICE")]
    [TestCase(GlobalBanSubjectKind.Uuid, "755A3C87-6F2B-4EC9-AED4-B8EEAF855C81", "755a3c876f2b4ec9aed4b8eeaf855c81")]
    public async Task MatchesTypedTextIdentity(GlobalBanSubjectKind kind, string subject, string identity)
    {
        var replica = new InMemoryGlobalBanReplica();
        var service = CreateService(replica);
        GlobalBanCreated created = Ban(kind, subject);
        await service.CreateAsync(created);
        var candidate = new BanIdentity(
            kind == GlobalBanSubjectKind.PlayerName ? identity : null,
            kind == GlobalBanSubjectKind.Account ? identity : null,
            kind == GlobalBanSubjectKind.Uuid ? identity : null,
            null);

        Assert.That((await service.FindActiveBanAsync(candidate))?.Id, Is.EqualTo(created.BanId));
    }

    [Test]
    public async Task OpaqueTerrariaUuidIsPreservedAndMatchedExactly()
    {
        const string opaqueUuid = " client-UUID-with-non-guid-format ";
        var replica = new InMemoryGlobalBanReplica();
        var service = CreateService(replica);
        GlobalBanCreated created = Ban(GlobalBanSubjectKind.Uuid, opaqueUuid);

        Assert.That(await service.CreateAsync(created), Is.True);
        BanSnapshot stored = service.SnapshotActive(Now).Single();
        BanSnapshot? exact = await service.FindActiveBanAsync(
            new BanIdentity(null, null, opaqueUuid, null));
        BanSnapshot? changedCase = await service.FindActiveBanAsync(
            new BanIdentity(null, null, opaqueUuid.ToLowerInvariant(), null));

        Assert.Multiple(() =>
        {
            Assert.That(stored.Subject.Value, Is.EqualTo(opaqueUuid));
            Assert.That(exact?.Id, Is.EqualTo(created.BanId));
            Assert.That(changedCase, Is.Null);
        });
    }

    [Test]
    public async Task ExcludesExpiredBan()
    {
        var replica = new InMemoryGlobalBanReplica();
        var service = CreateService(replica);
        await service.CreateAsync(Ban(GlobalBanSubjectKind.PlayerName, "Alice") with
        {
            ExpiresAt = Now - TimeSpan.FromSeconds(1)
        });

        Assert.That(
            await service.FindActiveBanAsync(new BanIdentity("Alice", null, null, null)),
            Is.Null);
    }

    [TestCase(GlobalBanSubjectKind.IpAddress, "999.1.1.1")]
    [TestCase(GlobalBanSubjectKind.Network, "192.0.2.1/99")]
    public void RejectsMalformedTypedSubjects(GlobalBanSubjectKind kind, string value)
    {
        var service = CreateService(new InMemoryGlobalBanReplica());

        Assert.That(
            service.TryNormalizeSubject(new GlobalBanSubject(kind, value), out _, out string? error),
            Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void RejectsEmptyOrOverlongOpaqueUuidSubjects()
    {
        var service = CreateService(new InMemoryGlobalBanReplica());
        string overlong = new('x', ConnectionMetadataLimits.MaxClientUuidLength + 1);

        Assert.Multiple(() =>
        {
            Assert.That(service.TryNormalizeSubject(
                new GlobalBanSubject(GlobalBanSubjectKind.Uuid, " \t\r\n"), out _, out _), Is.False);
            Assert.That(service.TryNormalizeSubject(
                new GlobalBanSubject(GlobalBanSubjectKind.Uuid, overlong), out _, out _), Is.False);
            Assert.That(service.TryNormalizeSubject(
                new GlobalBanSubject(GlobalBanSubjectKind.Uuid, "not-a-guid-but-valid-terrarian-token"), out GlobalBanSubject? normalized, out _), Is.True);
            Assert.That(normalized?.Value, Is.EqualTo("not-a-guid-but-valid-terrarian-token"));
        });
    }

    private static GlobalBanService CreateService(IGlobalBanReplica replica) =>
        new(replica, new FrozenTimeProvider(Now));

    private static GlobalBanCreated Ban(GlobalBanSubjectKind kind, string value) =>
        new(
            GlobalBanId.New(),
            new GlobalBanSubject(kind, value),
            "abuse",
            "console",
            Now - TimeSpan.FromMinutes(1),
            null,
            1);

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
