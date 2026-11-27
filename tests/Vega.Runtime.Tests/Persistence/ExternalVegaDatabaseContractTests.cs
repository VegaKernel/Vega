extern alias LegacyBCrypt;
using NUnit.Framework;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Authorization;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Moderation;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Contracts.Players;
using Vega.Persistence;
using Vega.Persistence.Sqlite;

namespace Vega.Runtime.Tests.Persistence;

[TestFixture]
public sealed class ExternalVegaDatabaseContractTests
{
    [TestCase("postgresql", "VEGA_TEST_POSTGRESQL")]
    [TestCase("mysql", "VEGA_TEST_MYSQL")]
    public async Task EmptyProviderInitializesOneBaselineReopensAndCreatesLogicalDump(
        string provider,
        string environmentVariable)
    {
        string? connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore($"Set {environmentVariable} to run the disposable {provider} contract test.");
        }

        var options = VegaDatabaseOptions.Default with
        {
            Provider = provider,
            ConnectionString = connectionString
        };
        await AssertProviderContractAsync(options, TestContext.CurrentContext.WorkDirectory);
    }

    [Test]
    public async Task SqliteRunsTheSamePersistenceAndBackupContract()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sqlite-provider-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AssertProviderContractAsync(VegaDatabaseOptions.Default, root);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertProviderContractAsync(VegaDatabaseOptions options, string baseDirectory)
    {
        IVegaDatabase first = VegaDatabaseFactory.Create(options, baseDirectory);
        await first.InitializeAsync();
        var created = await first.Authorization.CreateGroupAsync("builders", "default", "contract-test");
        Assert.That(created.Succeeded, Is.True);
        var style = new GroupChatPresentation("[B] ", "!", "12AB34");
        var presentation = await first.Authorization.SetPresentationAsync("builders", style, created.After!.Revision, "contract-test");
        Assert.That(presentation.Succeeded, Is.True);
        var allowance = await first.Authorization.SetPermissionAsync("builders", "vega.restrictions.allow.item.8",
            PermissionEffect.Grant, presentation.After!.Revision, "contract-test");
        Assert.That(allowance.Succeeded, Is.True);
        await AssertOfflineImportFamiliesAsync(first);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var importedAccount = first.Accounts.SnapshotAccounts().Single();
        var loggedIn = await first.Accounts.RecordLoginWithConnectionAsync(importedAccount.Name, now, importedAccount.Revision, "contract-test", "192.0.2.25", "contract-uuid");
        Assert.That(loggedIn.Succeeded, Is.True);
        Assert.That(await first.PluginData.CommitAsync("contract-plugin",
            [new("wallets", "account-1", 0, "100"), new("wallets", "account-2", 0, "10")]), Is.True);
        Assert.That(await first.PluginData.CommitAsync("contract-plugin",
            [new("wallets", "account-1", 1, "80"), new("wallets", "account-2", 2, "30")]), Is.False);
        var lease = await first.RoleLeases.GrantAsync(first.Accounts.SnapshotAccounts().Single().Id,
            "builders", now, now.AddHours(2), "contract-test");
        Assert.That(lease.Succeeded, Is.True);
        var mute = new MuteSnapshot(MuteId.New(), MuteSubjectKind.Account, "fixture-user", "fixture",
            "contract-test", now, now.AddHours(2), 1) { IpAddress = "192.0.2.25", Uuid = "contract-uuid" };
        Assert.That((await first.Mutes.CreateAsync(mute)).Succeeded, Is.True);
        var region = await first.Regions.CreateAsync(WorldId.Primary, "build-zone", 100, 100, 10, 10, 1, null, "contract-test");
        Assert.That(region.Succeeded, Is.True);
        var access = await first.Regions.SetAccessAsync(WorldId.Primary, "build-zone", RegionAccessKind.Group,
            "builders", true, region.After!.Revision, "contract-test");
        Assert.That(access.Succeeded, Is.True);
        var renamed = await first.Regions.RenameAsync(WorldId.Primary, "build-zone", "new-zone", access.After!.Revision, "contract-test");
        Assert.That(renamed.Succeeded, Is.True);

        var accountId = first.Accounts.SnapshotAccounts().Single().Id;
        Guid homeWorld = Guid.NewGuid();
        Assert.That(await first.Homes.SetAsync(new(accountId, WorldId.Primary, homeWorld, "base", new(1400, 1600)), 1), Is.EqualTo(HomeWriteResult.Applied));
        Assert.That(await first.Homes.SetAsync(new(accountId, WorldId.Primary, homeWorld, "second", new(1400, 1600)), 1), Is.EqualTo(HomeWriteResult.LimitReached));
        var tenure = new PlayerPlaytimeCheckpoint(Guid.NewGuid(), accountId, PlayerSessionId.New(), WorldId.Primary,
            now, TimeSpan.FromSeconds(40), false);
        var arena = tenure with { AuthenticationId = Guid.NewGuid(), SessionId = PlayerSessionId.New(), WorldId = new("Arena"),
            Elapsed = TimeSpan.FromSeconds(80), Ended = true };
        await first.Playtime.WriteAsync([tenure, arena]);
        await first.Playtime.WriteAsync([tenure with { Elapsed = TimeSpan.FromSeconds(70) }, tenure]);
        await first.Playtime.WriteAsync([tenure with { Elapsed = TimeSpan.FromSeconds(90), Ended = true }]);
        await first.Playtime.WriteAsync([tenure with { Elapsed = TimeSpan.FromSeconds(120) }, arena, arena with { WorldId = new("foreign") }]);

        IVegaDatabase reopened = VegaDatabaseFactory.Create(options, baseDirectory);
        await reopened.InitializeAsync();
        Assert.That((await reopened.Homes.FindAsync(accountId, WorldId.Primary, homeWorld, "BASE"))?.Position, Is.EqualTo(new PlayerPosition(1400, 1600)));
        Assert.That((await reopened.PluginData.ReadAsync("Contract-Plugin", "wallets", "account-1"))?.Json, Is.EqualTo("100"));
        Assert.That(await reopened.PluginData.ReadAsync("other-plugin", "wallets", "account-1"), Is.Null);
        Assert.That((await reopened.PluginData.ListAsync("contract-plugin", "wallets", "account-1", 1)).Single().Key, Is.EqualTo("account-2"));
        Assert.That(await reopened.Playtime.ReadAsync(accountId, WorldId.Primary), Is.EqualTo(new PlayerPlaytimeSummary(
            accountId, WorldId.Primary, TimeSpan.FromSeconds(170), TimeSpan.FromSeconds(90), 2, 1)));
        Assert.That((await reopened.Playtime.ReadAsync(accountId, new("arena"))).InWorld, Is.EqualTo(TimeSpan.Zero));
        Assert.That((await reopened.Playtime.ReadAsync(accountId, new("Arena"))).InWorld, Is.EqualTo(TimeSpan.FromSeconds(80)));
        string directory = Path.Combine(Path.GetTempPath(), "vega-external-dump-tests", Guid.NewGuid().ToString("N"));
        string dump = Path.Combine(directory, reopened.BackupFileName);
        try
        {
            await reopened.WriteBackupAsync(dump);
            Assert.Multiple(() =>
            {
                Assert.That(first.SchemaVersion, Is.EqualTo(1));
                Assert.That(reopened.SchemaVersion, Is.EqualTo(1));
                Assert.That(created.Succeeded, Is.True);
                Assert.That(reopened.Authorization.SnapshotGroups().Any(group => group.Name == "builders"), Is.True);
                Assert.That(reopened.Authorization.SnapshotGroups().Single(group => group.Name == "builders").Presentation, Is.EqualTo(style));
                Assert.That(reopened.Authorization.HasPermission("builders", "vega.restrictions.allow.item.8"), Is.True);
                var persistedRegion = reopened.Regions.SnapshotRegions(WorldId.Primary).Single(entry => entry.Name == "new-zone");
                Assert.That(persistedRegion.Id, Is.EqualTo(region.After.Id));
                Assert.That(persistedRegion.Groups, Does.Contain("builders"));
                Assert.That(reopened.Accounts.SnapshotAccounts().Single().Id.Value, Is.EqualTo(42));
                Assert.That(reopened.RoleLeases.FindActive(first.Accounts.SnapshotAccounts().Single().Id, now)!.Id,
                    Is.EqualTo(lease.After!.Id));
                Assert.That(reopened.Mutes.FindActive("other-name", "fixture-user", now)!.Id, Is.EqualTo(mute.Id));
                Assert.That(reopened.Mutes.FindActive("different-name", null, "192.0.2.25", null, now)!.Id, Is.EqualTo(mute.Id));
                Assert.That(reopened.Mutes.FindActive("different-name", null, null, "contract-uuid", now)!.Id, Is.EqualTo(mute.Id));
                Assert.That(reopened.Accounts.SnapshotAccounts().Single().LastKnownIp, Is.EqualTo("192.0.2.25"));
                var imported = reopened.Regions.SnapshotRegions(WorldId.Primary).Single(entry => entry.Name == "imported-zone");
                Assert.That(imported.Id, Is.EqualTo(91));
                Assert.That(imported.Owner, Is.EqualTo("fixture-user"));
                Assert.That(imported.Members, Does.Contain("fixture-user"));
                Assert.That(imported.Groups, Does.Contain("builders"));
                Assert.That(new FileInfo(dump).Length, Is.GreaterThan(0));
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    internal static async Task AssertOfflineImportFamiliesAsync(IVegaDatabase database)
    {
        Assert.That(database, Is.InstanceOf<IVegaOfflineImport>());
        var import = (IVegaOfflineImport)database;
        const string password = "test-only-provider-fixture";
        var hash = LegacyBCrypt::BCrypt.Net.BCrypt.HashPassword(password, 4);
        var account = new LegacyAccountImport(42, "fixture-user", "builders", hash, null);
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportAccountsAsync(
            [account, new LegacyAccountImport(43, "unmapped-user", "unknown-group", hash, null)]));
        Assert.That(database.Accounts.SnapshotAccounts(), Is.Empty);
        Assert.That(await import.ImportAccountsAsync([account]), Is.EqualTo(1));
        Assert.That(database.Accounts.SnapshotAccounts().Single().Id.Value, Is.EqualTo(42));
        Assert.That(await database.Accounts.VerifyPasswordAsync("fixture-user", password), Is.True);
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportAccountsAsync([account]));

        var region = new RegionSnapshot(91, "imported-zone", 10, 20, 30, 40, "FIXTURE-USER")
        {
            Priority = 5,
            Members = new HashSet<string> { "FIXTURE-USER" },
            Groups = new HashSet<string> { "Builders" },
            Flags = new Dictionary<string, bool> { ["protected"] = true }
        };
        // Initialization has already populated the primary-region cache.
        Assert.That(database.Regions.SnapshotRegions(WorldId.Primary), Is.Empty);
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportPrimaryRegionsAsync(
            [region, region with { Id = 92, Name = "invalid-zone", Owner = "unknown-account" }]));
        Assert.That(database.Regions.SnapshotRegions(WorldId.Primary), Is.Empty);
        var sandboxWorld = new WorldId("import-contract-sandbox");
        var sandbox = await database.Regions.CreateAsync(sandboxWorld, "sandbox-zone", 1, 1, 10, 10, 1, null, "contract-test");
        Assert.That(sandbox.Succeeded, Is.True);
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportPrimaryRegionsAsync(
            [region with { Id = sandbox.After!.Id }]));
        Assert.That(database.Regions.SnapshotRegions(sandboxWorld).Single().Id, Is.EqualTo(sandbox.After!.Id));
        // The initial rows and ACLs must be rolled back even when a later SQL
        // constraint fails after insert. Canonical names collapse these two entries.
        Assert.CatchAsync<DbException>(async () => await import.ImportPrimaryRegionsAsync(
            [region, region with { Id = 92, Name = "duplicate-acl-zone", Members = new HashSet<string>(StringComparer.Ordinal)
                { "fixture-user", "FIXTURE-USER" } }]));
        Assert.That(database.Regions.SnapshotRegions(WorldId.Primary), Is.Empty);
        Assert.That(await import.ImportPrimaryRegionsAsync([region]), Is.EqualTo(1));
        var imported = database.Regions.SnapshotRegions(WorldId.Primary).Single();
        Assert.That(imported.Id, Is.EqualTo(91));
        Assert.That(imported.Members, Does.Contain("fixture-user"));
        Assert.That(imported.Groups, Does.Contain("builders"));
        Assert.That(imported.Flags["protected"], Is.True);
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportPrimaryRegionsAsync([region]));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var ban = new GlobalBanCreated(new GlobalBanId(Guid.NewGuid()),
            new GlobalBanSubject(GlobalBanSubjectKind.PlayerName, "fixture-user"), "test", "operator", now, now.AddDays(1), 1);
        var other = ban with { BanId = new GlobalBanId(Guid.NewGuid()), Reason = "other fixture" };
        Assert.ThrowsAsync<InvalidDataException>(async () => await import.ImportBansAsync([ban, ban]));
        Assert.That(await import.ImportBansAsync([ban, other]), Is.EqualTo(2));
        Assert.That(await database.GlobalBans.ApplyRevokedAsync(new(ban.BanId, "operator", now, 2)), Is.True);
        Assert.That(await import.ImportBansAsync([ban with { Revision = 3 }, other with { Reason = "must not replace", Revision = 3 }]), Is.Zero);
        var active = database.GlobalBans.SnapshotActive(now);
        Assert.That(active.Any(row => row.BanId == ban.BanId), Is.False);
        Assert.That(active.Single(row => row.BanId == other.BanId).Reason, Is.EqualTo(other.Reason));
    }
}
