using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Persistence;

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
                Assert.That(new FileInfo(dump).Length, Is.GreaterThan(0));
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
