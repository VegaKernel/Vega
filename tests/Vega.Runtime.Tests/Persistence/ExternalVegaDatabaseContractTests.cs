extern alias LegacyBCrypt;
using NUnit.Framework;
using System.Data.Common;
using Vega.Contracts.Authorization;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
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
        IVegaDatabase first = VegaDatabaseFactory.Create(options, TestContext.CurrentContext.WorkDirectory);
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
        var region = await first.Regions.CreateAsync(WorldId.Primary, "build-zone", 100, 100, 10, 10, 1, null, "contract-test");
        Assert.That(region.Succeeded, Is.True);
        var access = await first.Regions.SetAccessAsync(WorldId.Primary, "build-zone", RegionAccessKind.Group,
            "builders", true, region.After!.Revision, "contract-test");
        Assert.That(access.Succeeded, Is.True);
        var renamed = await first.Regions.RenameAsync(WorldId.Primary, "build-zone", "new-zone", access.After!.Revision, "contract-test");
        Assert.That(renamed.Succeeded, Is.True);

        IVegaDatabase reopened = VegaDatabaseFactory.Create(options, TestContext.CurrentContext.WorkDirectory);
        await reopened.InitializeAsync();
        string directory = Path.Combine(Path.GetTempPath(), "vega-external-dump-tests", Guid.NewGuid().ToString("N"));
        string dump = Path.Combine(directory, reopened.BackupFileName);
        try
        {
            await reopened.WriteBackupAsync(dump);
            Assert.Multiple(() =>
            {
                Assert.That(first.SchemaVersion, Is.EqualTo(2));
                Assert.That(reopened.SchemaVersion, Is.EqualTo(2));
                Assert.That(created.Succeeded, Is.True);
                Assert.That(reopened.Authorization.SnapshotGroups().Any(group => group.Name == "builders"), Is.True);
                Assert.That(reopened.Authorization.SnapshotGroups().Single(group => group.Name == "builders").Presentation, Is.EqualTo(style));
                Assert.That(reopened.Authorization.HasPermission("builders", "vega.restrictions.allow.item.8"), Is.True);
                var persistedRegion = reopened.Regions.SnapshotRegions(WorldId.Primary).Single(entry => entry.Name == "new-zone");
                Assert.That(persistedRegion.Id, Is.EqualTo(region.After.Id));
                Assert.That(persistedRegion.Groups, Does.Contain("builders"));
                Assert.That(reopened.Accounts.SnapshotAccounts().Single().Id.Value, Is.EqualTo(42));
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
